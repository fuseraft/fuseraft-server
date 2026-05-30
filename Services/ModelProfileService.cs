using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using fuseraft.Core;
using fuseraft.Server.Models;

namespace fuseraft.Server.Services;

/// <summary>
/// Manages stored model profiles (name, model ID, endpoint, provider) with API keys
/// held in the OS keychain. On startup all API keys are injected as environment variables
/// so the orchestrator finds them via the profile's <see cref="ModelProfile.ApiKeyEnvVar"/>.
/// </summary>
public sealed class ModelProfileService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented             = true,
        PropertyNamingPolicy      = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static string ProfilesFilePath =>
        Path.Combine(FuseraftPaths.GlobalRoot, "model-profiles.json");

    private readonly List<ModelProfile> _profiles = [];
    private readonly SemaphoreSlim      _lock     = new(1, 1);

    public event Action? ProfilesChanged;

    public ModelProfileService() => _ = LoadAsync();

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
            foreach (var p in _profiles)
            {
                var key = await RetrieveKeyAsync(p.Id);
                if (!string.IsNullOrEmpty(key))
                    Environment.SetEnvironmentVariable(p.ApiKeyEnvVar, key);
            }
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
            {
                await StoreKeyAsync(profile.Id, apiKey);
                Environment.SetEnvironmentVariable(profile.ApiKeyEnvVar, apiKey);
            }
        }
        finally { _lock.Release(); ProfilesChanged?.Invoke(); }
    }

    public async Task DeleteAsync(string id)
    {
        ModelProfile? found = null;
        await _lock.WaitAsync();
        try
        {
            found = _profiles.FirstOrDefault(p => p.Id == id);
            _profiles.RemoveAll(p => p.Id == id);
            await PersistAsync();
            await DeleteKeyAsync(id);
        }
        finally { _lock.Release(); }

        if (found is not null)
            Environment.SetEnvironmentVariable(found.ApiKeyEnvVar, null);
        ProfilesChanged?.Invoke();
    }

    public async Task<bool> HasKeyAsync(string id) =>
        !string.IsNullOrEmpty(await RetrieveKeyAsync(id));

    // ── Persistence ─────────────────────────────────────────────────────────────

    private async Task PersistAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilesFilePath)!);
        var json = JsonSerializer.Serialize(_profiles, JsonOpts);
        await File.WriteAllTextAsync(ProfilesFilePath, json);
    }

    // ── Platform-specific keychain storage ──────────────────────────────────────

    private const string KeyService = "fuseraft-server";
    private static string AccountName(string profileId) => $"profile-{profileId}";

    private static async Task StoreKeyAsync(string profileId, string apiKey)
    {
        try
        {
            if (OperatingSystem.IsWindows())  { WindowsStoreKey(profileId, apiKey); return; }
            if (OperatingSystem.IsMacOS())    { await MacStoreKeyAsync(profileId, apiKey); return; }
            if (OperatingSystem.IsLinux() && await SecretToolAvailableAsync())
            {
                await LinuxStoreKeyAsync(profileId, apiKey);
                return;
            }
        }
        catch { /* fall through to plain-text */ }
        await PlainTextStoreAsync(profileId, apiKey);
    }

    private static async Task<string?> RetrieveKeyAsync(string profileId)
    {
        try
        {
            if (OperatingSystem.IsWindows())  return WindowsRetrieveKey(profileId);
            if (OperatingSystem.IsMacOS())    return await MacRetrieveKeyAsync(profileId);
            if (OperatingSystem.IsLinux() && await SecretToolAvailableAsync())
                return await LinuxRetrieveKeyAsync(profileId);
        }
        catch { }
        return await PlainTextRetrieveAsync(profileId);
    }

    private static async Task DeleteKeyAsync(string profileId)
    {
        try
        {
            if (OperatingSystem.IsWindows())  { WindowsDeleteKey(profileId); }
            else if (OperatingSystem.IsMacOS()) { await MacDeleteKeyAsync(profileId); }
            else if (OperatingSystem.IsLinux() && await SecretToolAvailableAsync())
                { await LinuxDeleteKeyAsync(profileId); }
        }
        catch { }
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

    private static async Task LinuxStoreKeyAsync(string profileId, string apiKey)
    {
        var account = AccountName(profileId);
        var psi = new ProcessStartInfo("secret-tool",
            $"store --label \"fuseraft model profile\" service {KeyService} account {account}")
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

    private static async Task<string?> LinuxRetrieveKeyAsync(string profileId)
    {
        var account = AccountName(profileId);
        var (exit, stdout, _) = await RunAsync("secret-tool",
            $"lookup service {KeyService} account {account}");
        return exit == 0 && !string.IsNullOrEmpty(stdout) ? stdout.Trim() : null;
    }

    private static async Task LinuxDeleteKeyAsync(string profileId)
    {
        var account = AccountName(profileId);
        await RunAsync("secret-tool", $"clear service {KeyService} account {account}");
    }

    // ── macOS (security CLI) ─────────────────────────────────────────────────────

    private static async Task MacStoreKeyAsync(string profileId, string apiKey)
    {
        var account = AccountName(profileId);
        var (exit, _, stderr) = await RunAsync("security",
            $"add-generic-password -s \"{KeyService}\" -a \"{account}\" -w \"{apiKey}\" -U");
        if (exit != 0)
            throw new InvalidOperationException($"security add-generic-password failed: {stderr.Trim()}");
    }

    private static async Task<string?> MacRetrieveKeyAsync(string profileId)
    {
        var account = AccountName(profileId);
        var (exit, stdout, _) = await RunAsync("security",
            $"find-generic-password -s \"{KeyService}\" -a \"{account}\" -w");
        return exit == 0 && !string.IsNullOrEmpty(stdout) ? stdout.Trim() : null;
    }

    private static async Task MacDeleteKeyAsync(string profileId)
    {
        var account = AccountName(profileId);
        await RunAsync("security",
            $"delete-generic-password -s \"{KeyService}\" -a \"{account}\"");
    }

    // ── Windows (Credential Manager) ─────────────────────────────────────────────

    private static string WinTarget(string profileId) => $"{KeyService}/profile-{profileId}";

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WindowsStoreKey(string profileId, string apiKey)
    {
        var blob      = Encoding.Unicode.GetBytes(apiKey);
        var target    = WinTarget(profileId);
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
    private static string? WindowsRetrieveKey(string profileId)
    {
        var target = WinTarget(profileId);
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
    private static void WindowsDeleteKey(string profileId)
        => CredDelete(WinTarget(profileId), 1, 0);

    // ── Plain-text fallback (~/.fuseraft/profile-keys/{id}, mode 600) ────────────

    private static string PlainTextDir =>
        Path.Combine(FuseraftPaths.GlobalRoot, "profile-keys");

    private static string PlainTextPath(string profileId) =>
        Path.Combine(PlainTextDir, profileId);

    private static async Task PlainTextStoreAsync(string profileId, string apiKey)
    {
        Directory.CreateDirectory(PlainTextDir);
        var path = PlainTextPath(profileId);
        await File.WriteAllTextAsync(path, apiKey, Encoding.UTF8);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task<string?> PlainTextRetrieveAsync(string profileId)
    {
        var path = PlainTextPath(profileId);
        if (!File.Exists(path)) return null;
        try { return (await File.ReadAllTextAsync(path, Encoding.UTF8)).Trim(); }
        catch { return null; }
    }

    private static void PlainTextDelete(string profileId)
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
