using System.Security.Cryptography;
using System.Text.Json;
using fuseraft.Web.Models;
using Microsoft.AspNetCore.DataProtection;

namespace fuseraft.Web.Services.Daemons;

/// <summary>
/// <c>&lt;data&gt;/daemons.json</c>: the daemons added on the Daemons page, and the client id this server attaches to
/// each daemon with, so a restarted server picks its tasks up again. Tokens are encrypted with the server's
/// data-protection keys.
/// </summary>
public sealed class DaemonStore(string path, IDataProtectionProvider protection, ILogger<DaemonStore>? logger = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private const int MaxTaskOwners = 500;

    private readonly IDataProtector _protector = protection.CreateProtector("fuseraft-web.daemons.token");
    private readonly Lock _lock = new();
    private StoreFile? _file;

    /// <summary>Daemons added on the page, with their tokens decrypted.</summary>
    public IReadOnlyList<DaemonEndpoint> Added()
    {
        lock (_lock)
            return [.. File.Added.Select(d => new DaemonEndpoint
            {
                Name              = d.Name,
                Url               = d.Url,
                Token             = Unprotect(d),
                CertificateSha256 = d.CertificateSha256,
                AutoConnect       = d.AutoConnect,
                Users             = [.. d.Users],
            })];
    }

    /// <summary>The client id for the daemon called <paramref name="name"/>, made and saved the first time.</summary>
    public string ClientId(string name)
    {
        lock (_lock)
        {
            var key = name.ToLowerInvariant();
            if (File.ClientIds.TryGetValue(key, out var id)) return id;
            id = Guid.NewGuid().ToString("N");
            File.ClientIds[key] = id;
            Save();
            return id;
        }
    }

    public void SaveAdded(IEnumerable<DaemonEndpoint> added)
    {
        lock (_lock)
        {
            File.Added = [.. added.Select(d => new SavedDaemon
            {
                Name              = d.Name,
                Url               = d.Url,
                Token             = d.Token is null ? null : _protector.Protect(d.Token),
                CertificateSha256 = d.CertificateSha256,
                AutoConnect       = d.AutoConnect,
                Users             = [.. d.Users],
            })];
            Save();
        }
    }

    /// <summary>Forgets the daemon's client id and task owners, so one added again under the name starts afresh.</summary>
    public void Forget(string name)
    {
        lock (_lock)
        {
            var key = name.ToLowerInvariant();
            if (File.ClientIds.Remove(key) | File.TaskOwners.Remove(key)) Save();
        }
    }

    /// <summary>Who sent the daemon's task <paramref name="sessionId"/>, while it's unfinished.</summary>
    public string? TaskOwner(string daemon, string sessionId)
    {
        lock (_lock)
            return File.TaskOwners.GetValueOrDefault(daemon.ToLowerInvariant())?.GetValueOrDefault(sessionId);
    }

    /// <summary>Remembers who sent a task, so after a restart its approvals still go to them; null forgets it.</summary>
    public void SetTaskOwner(string daemon, string sessionId, string? ownerId)
    {
        lock (_lock)
        {
            var key = daemon.ToLowerInvariant();
            if (ownerId is not null)
            {
                if (!File.TaskOwners.TryGetValue(key, out var owners)) File.TaskOwners[key] = owners = [];
                owners[sessionId] = ownerId;
                // Tasks whose result never came (lost with a daemon that went away for good) would otherwise pile up.
                while (owners.Count > MaxTaskOwners) owners.Remove(owners.Keys.First());
            }
            else if (File.TaskOwners.GetValueOrDefault(key)?.Remove(sessionId) != true) return;
            Save();
        }
    }

    private StoreFile File => _file ??= Load();

    private StoreFile Load()
    {
        if (!System.IO.File.Exists(path)) return new StoreFile();
        try
        {
            return JsonSerializer.Deserialize<StoreFile>(System.IO.File.ReadAllText(path), Json) ?? new StoreFile();
        }
        catch (JsonException ex)
        {
            logger?.LogError(ex, "{Path} isn't valid JSON; starting with no saved daemons", path);
            return new StoreFile();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(File, Json));
        if (!OperatingSystem.IsWindows())
            System.IO.File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        System.IO.File.Move(temp, path, overwrite: true);
    }

    private string? Unprotect(SavedDaemon d)
    {
        if (d.Token is null) return null;
        try { return _protector.Unprotect(d.Token); }
        catch (CryptographicException)
        {
            logger?.LogWarning("The token saved for daemon {Name} can't be decrypted (the server's keys changed); add it again", d.Name);
            return null;
        }
    }

    private sealed class StoreFile
    {
        public List<SavedDaemon> Added { get; set; } = [];
        public Dictionary<string, string> ClientIds { get; set; } = [];
        /// <summary>Daemon name → unfinished task's session id → the user id that sent it.</summary>
        public Dictionary<string, Dictionary<string, string>> TaskOwners { get; set; } = [];
    }

    private sealed class SavedDaemon
    {
        public string  Name  { get; set; } = "";
        public string  Url   { get; set; } = "";
        /// <summary>Encrypted.</summary>
        public string? Token { get; set; }
        public string? CertificateSha256 { get; set; }
        public bool    AutoConnect { get; set; } = true;
        public List<string> Users { get; set; } = [];
    }
}
