using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using fuseraft.Web.Models;

namespace fuseraft.Web.Services;

/// <summary>
/// One user's model profiles (name, model ID, endpoint, provider), kept in their fuseraft home, with API keys
/// held in the OS keychain. Keys are handed only to the processes that user starts, through
/// <see cref="KeyEnvironmentAsync"/>, never set on the server's own environment.
/// </summary>
public sealed class ModelProfileService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented             = true,
        PropertyNamingPolicy      = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private string ProfilesFilePath => Path.Combine(_home, "model-profiles.json");

    private readonly List<ModelProfile> _profiles = [];
    private readonly SemaphoreSlim      _lock     = new(1, 1);
    private readonly string _home;
    private readonly string _keyNamespace;
    private readonly bool _useKeychain;

    public event Action? ProfilesChanged;

    /// <param name="home">The fuseraft home the profiles live in.</param>
    /// <param name="keyNamespace">Prefixes keychain entries, which the OS user's accounts all share.</param>
    /// <param name="useKeychain">False keeps keys in the home's plain-text fallback, so tests never touch the real keychain.</param>
    public ModelProfileService(string home, string keyNamespace, bool useKeychain = true)
    {
        _home         = home;
        _keyNamespace = keyNamespace;
        _useKeychain  = useKeychain;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(ProfilesFilePath)) return;
            var json   = await File.ReadAllTextAsync(ProfilesFilePath);
            var loaded = JsonSerializer.Deserialize<List<ModelProfile>>(json, JsonOpts) ?? [];
            _profiles.Clear();
            _profiles.AddRange(loaded);
        }
        catch { /* load is best-effort; corrupt file is silently skipped */ }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<ModelProfile>> ListAsync()
    {
        await _lock.WaitAsync();
        try { return [.. _profiles]; }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(ModelProfile profile, string? apiKey)
    {
        await _lock.WaitAsync();
        try
        {
            var idx = _profiles.FindIndex(p => p.Id == profile.Id);
            if (idx >= 0) _profiles[idx] = profile;
            else          _profiles.Add(profile);
            await PersistAsync();

            if (!string.IsNullOrEmpty(apiKey))
                await StoreKeyAsync(profile.Id, apiKey);
        }
        finally { _lock.Release(); ProfilesChanged?.Invoke(); }
    }

    public async Task DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            _profiles.RemoveAll(p => p.Id == id);
            await PersistAsync();
            await DeleteKeyAsync(id);
        }
        finally { _lock.Release(); }
        ProfilesChanged?.Invoke();
    }

    public async Task<bool> HasKeyAsync(string id) =>
        !string.IsNullOrEmpty(await RetrieveKeyAsync(id));

    public Task<string?> GetKeyAsync(string id) => RetrieveKeyAsync(id);

    /// <summary>
    /// Every profile's key as <see cref="ModelProfile.ApiKeyEnvVar"/>, for the environment of a process this user
    /// starts, so a config's <c>apiKeyEnvVar: FUSERAFT_PROFILE_{id}_KEY</c> resolves.
    /// </summary>
    public async Task<Dictionary<string, string>> KeyEnvironmentAsync()
    {
        var env = new Dictionary<string, string>();
        foreach (var p in await ListAsync())
            if (await RetrieveKeyAsync(p.Id) is { Length: > 0 } key)
                env[p.ApiKeyEnvVar] = key;
        return env;
    }

    // ── Persistence ─────────────────────────────────────────────────────────────

    private async Task PersistAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilesFilePath)!);
        var json = JsonSerializer.Serialize(_profiles, JsonOpts);
        await File.WriteAllTextAsync(ProfilesFilePath, json);
    }

    // ── Platform-specific keychain storage ──────────────────────────────────────

    private const string KeyService = "fuseraft-web";
    // Where keys lived before accounts existed; read once, then moved under KeyService.
    private const string LegacyKeyService = "fuseraft-server";
    private string AccountName(string profileId) => $"{_keyNamespace}profile-{profileId}";
    private static string LegacyAccountName(string profileId) => $"profile-{profileId}";

    private async Task StoreKeyAsync(string profileId, string apiKey)
    {
        if (_useKeychain)
        {
            try
            {
                var account = AccountName(profileId);
                if (OperatingSystem.IsWindows()) { WindowsStoreKey(KeyService, account, apiKey); return; }
                if (OperatingSystem.IsMacOS())   { await MacStoreKeyAsync(KeyService, account, apiKey); return; }
                if (OperatingSystem.IsLinux() && await SecretToolAvailableAsync())
                {
                    await LinuxStoreKeyAsync(KeyService, account, apiKey);
                    return;
                }
            }
            catch { /* fall through to plain-text */ }
        }
        await PlainTextStoreAsync(profileId, apiKey);
    }

    private async Task<string?> RetrieveKeyAsync(string profileId)
    {
        if (!_useKeychain) return await PlainTextRetrieveAsync(profileId);
        if (await KeychainRetrieveAsync(KeyService, AccountName(profileId)) is { Length: > 0 } key)
            return key;
        if (_home == FuseraftHome.Root &&
            await KeychainRetrieveAsync(LegacyKeyService, LegacyAccountName(profileId)) is { Length: > 0 } legacy)
        {
            await StoreKeyAsync(profileId, legacy);
            await KeychainDeleteAsync(LegacyKeyService, LegacyAccountName(profileId));
            return legacy;
        }
        return await PlainTextRetrieveAsync(profileId);
    }

    private static async Task<string?> KeychainRetrieveAsync(string service, string account)
    {
        try
        {
            if (OperatingSystem.IsWindows())  return WindowsRetrieveKey(service, account);
            if (OperatingSystem.IsMacOS())    return await MacRetrieveKeyAsync(service, account);
            if (OperatingSystem.IsLinux() && await SecretToolAvailableAsync())
                return await LinuxRetrieveKeyAsync(service, account);
        }
        catch { }
        return null;
    }

    private static async Task KeychainDeleteAsync(string service, string account)
    {
        try
        {
            if (OperatingSystem.IsWindows())  WindowsDeleteKey(service, account);
            else if (OperatingSystem.IsMacOS()) await MacDeleteKeyAsync(service, account);
            else if (OperatingSystem.IsLinux() && await SecretToolAvailableAsync())
                await LinuxDeleteKeyAsync(service, account);
        }
        catch { }
    }

    private async Task DeleteKeyAsync(string profileId)
    {
        if (_useKeychain)
        {
            await KeychainDeleteAsync(KeyService, AccountName(profileId));
            await KeychainDeleteAsync(LegacyKeyService, LegacyAccountName(profileId));
        }
        PlainTextDelete(profileId);
    }

    // ── Linux (secret-tool) ─────────────────────────────────────────────────────

    private static bool? _secretToolAvailable;

    private static async Task<bool> SecretToolAvailableAsync()
    {
        if (_secretToolAvailable.HasValue) return _secretToolAvailable.Value;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("secret-tool", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            });
            _secretToolAvailable = p?.WaitForExit(3000) == true;
        }
        catch { _secretToolAvailable = false; }
        return _secretToolAvailable!.Value;
    }

    private static async Task LinuxStoreKeyAsync(string service, string account, string apiKey)
    {
        var psi = new ProcessStartInfo("secret-tool",
            $"store --label \"fuseraft model profile\" service {service} account {account}")
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        using var p   = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);
        await p.StandardInput.WriteAsync(apiKey);
        p.StandardInput.Close();
        await p.WaitForExitAsync(cts.Token);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"secret-tool store failed: {(await stderrTask).Trim()}");
    }

    private static async Task<string?> LinuxRetrieveKeyAsync(string service, string account)
    {
        var (exit, stdout, _) = await RunAsync("secret-tool",
            $"lookup service {service} account {account}");
        return exit == 0 && !string.IsNullOrEmpty(stdout) ? stdout.Trim() : null;
    }

    private static async Task LinuxDeleteKeyAsync(string service, string account)
    {
        await RunAsync("secret-tool", $"clear service {service} account {account}");
    }

    // ── macOS (security CLI) ─────────────────────────────────────────────────────

    private static async Task MacStoreKeyAsync(string service, string account, string apiKey)
    {
        var (exit, _, stderr) = await RunAsync("security",
            $"add-generic-password -s \"{service}\" -a \"{account}\" -w \"{apiKey}\" -U");
        if (exit != 0)
            throw new InvalidOperationException($"security add-generic-password failed: {stderr.Trim()}");
    }

    private static async Task<string?> MacRetrieveKeyAsync(string service, string account)
    {
        var (exit, stdout, _) = await RunAsync("security",
            $"find-generic-password -s \"{service}\" -a \"{account}\" -w");
        return exit == 0 && !string.IsNullOrEmpty(stdout) ? stdout.Trim() : null;
    }

    private static async Task MacDeleteKeyAsync(string service, string account)
    {
        await RunAsync("security",
            $"delete-generic-password -s \"{service}\" -a \"{account}\"");
    }

    // ── Windows (Credential Manager) ─────────────────────────────────────────────

    private static string WinTarget(string service, string account) => $"{service}/{account}";

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WindowsStoreKey(string service, string account, string apiKey)
    {
        var blob      = Encoding.Unicode.GetBytes(apiKey);
        var target    = WinTarget(service, account);
        var blobPtr   = Marshal.AllocHGlobal(blob.Length);
        var targetPtr = Marshal.StringToHGlobalUni(target);
        var userPtr   = Marshal.StringToHGlobalUni("fuseraft");
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new NativeCredential
            {
                Type               = 1,
                TargetName         = targetPtr,
                UserName           = userPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob     = blobPtr,
                Persist            = 2,
            };
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException(
                    $"CredWrite failed (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? WindowsRetrieveKey(string service, string account)
    {
        var target = WinTarget(service, account);
        if (!CredRead(target, 1, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<NativeCredential>(ptr);
            if (cred.CredentialBlobSize == 0) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally { CredFree(ptr); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WindowsDeleteKey(string service, string account)
        => CredDelete(WinTarget(service, account), 1, 0);

    // ── Plain-text fallback ({home}/profile-keys/{id}, mode 600) ──────────────────

    private string PlainTextDir => Path.Combine(_home, "profile-keys");

    private string PlainTextPath(string profileId) => Path.Combine(PlainTextDir, profileId);

    private async Task PlainTextStoreAsync(string profileId, string apiKey)
    {
        Directory.CreateDirectory(PlainTextDir);
        var path = PlainTextPath(profileId);
        await File.WriteAllTextAsync(path, apiKey, Encoding.UTF8);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private async Task<string?> PlainTextRetrieveAsync(string profileId)
    {
        var path = PlainTextPath(profileId);
        if (!File.Exists(path)) return null;
        try { return (await File.ReadAllTextAsync(path, Encoding.UTF8)).Trim(); }
        catch { return null; }
    }

    private void PlainTextDelete(string profileId)
    {
        var path = PlainTextPath(profileId);
        if (File.Exists(path)) File.Delete(path);
    }

    // ── Shared process runner ────────────────────────────────────────────────────

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        using var p   = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stdoutTask = p.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);
        await Task.WhenAll(stdoutTask, stderrTask);
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    // ── P/Invoke (Windows Credential Manager) ────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint   Flags;
        public int    Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long   LastWritten;
        public uint   CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint   Persist;
        public uint   AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW",   CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reserved, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW",  CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int reserved);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredFree(IntPtr credential);
}

/// <summary>Each account's <see cref="ModelProfileService"/>, kept for the server's lifetime so their change events reach every tab.</summary>
public sealed class ModelProfileRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ModelProfileService> _byUser = new();

    internal bool UseKeychain { get; init; } = true;

    public ModelProfileService For(fuseraft.Web.Services.Users.WebUser user) =>
        _byUser.GetOrAdd(user.Id, _ => new ModelProfileService(user.Home, $"{user.Id}/", UseKeychain));
}
