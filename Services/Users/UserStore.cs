using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace fuseraft.Web.Services.Users;

/// <summary>
/// The accounts that can sign in, kept in <c>users.json</c> in the data directory (mode 600). The first account
/// gets fuseraft's default home, so a server that was used before accounts existed keeps its sessions and profiles.
/// </summary>
public sealed partial class UserStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    };

    // A password check for an unknown user still hashes, so its timing doesn't reveal which names exist.
    private static readonly Lazy<string> DummyHash = new(() => PasswordHashing.Hash(Guid.NewGuid().ToString()));

    private readonly Lock _lock = new();
    private readonly List<WebUser> _users = [];
    private readonly string _path;
    private DateTime _loadedWriteTime;
    private readonly string _dataDir;
    private readonly AuthOptions _auth;

    public UserStore(IOptions<WebOptions> web, IOptions<AuthOptions> auth)
    {
        _dataDir = web.Value.ResolvedDataDirectory;
        _auth    = auth.Value;
        _path    = Path.Combine(_dataDir, "users.json");
        Refresh();
        if (_users.Count == 0) SetupCode = NewCode();
    }

    /// <summary>
    /// Rereads <c>users.json</c> if something else changed it (<c>fuseraft-web users</c> while the server runs).
    /// Accounts are kept as the same objects, so a signed-in circuit sees a disable or a new stamp at once.
    /// </summary>
    private void Refresh()
    {
        lock (_lock)
        {
            var written = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : default;
            if (written == _loadedWriteTime) return;
            var loaded = written == default ? [] : JsonSerializer.Deserialize<List<WebUser>>(File.ReadAllText(_path), JsonOpts) ?? [];
            var merged = loaded.Select(fresh => _users.FirstOrDefault(u => u.Id == fresh.Id) is { } known ? CopyInto(known, fresh) : fresh).ToList();
            _users.Clear();
            _users.AddRange(merged);
            _loadedWriteTime = written;
            if (_users.Count > 0) SetupCode = null;
        }
    }

    private static WebUser CopyInto(WebUser known, WebUser fresh)
    {
        known.UserName       = fresh.UserName;
        known.DisplayName    = fresh.DisplayName;
        known.IsAdmin        = fresh.IsAdmin;
        known.Disabled       = fresh.Disabled;
        known.PasswordHash   = fresh.PasswordHash;
        known.Home           = fresh.Home;
        known.WorkspaceRoots = fresh.WorkspaceRoots;
        known.SecurityStamp  = fresh.SecurityStamp;
        known.Logins.Clear();
        known.Logins.AddRange(fresh.Logins);
        return known;
    }

    public event Action? Changed;

    public string DataDirectory => _dataDir;

    /// <summary>While there are no accounts: the code that lets someone create the first admin at <c>/setup</c>.</summary>
    public string? SetupCode { get; private set; }

    public IReadOnlyList<WebUser> All
    {
        get { Refresh(); lock (_lock) return [.. _users.OrderBy(u => u.UserName)]; }
    }

    public bool IsEmpty
    {
        get { Refresh(); lock (_lock) return _users.Count == 0; }
    }

    public WebUser? Find(string? id)
    {
        Refresh();
        lock (_lock) return _users.FirstOrDefault(u => u.Id == id);
    }

    public WebUser? FindByName(string? userName)
    {
        var name = Normalize(userName);
        Refresh();
        lock (_lock) return _users.FirstOrDefault(u => u.UserName == name);
    }

    public WebUser? FindByLogin(string issuer, string subject)
    {
        Refresh();
        lock (_lock) return _users.FirstOrDefault(u => u.Logins.Any(l => l.Issuer == issuer && l.Subject == subject));
    }

    /// <summary>The enabled account with this name and password, or null.</summary>
    public WebUser? CheckPassword(string? userName, string? password)
    {
        var user = FindByName(userName);
        var ok   = PasswordHashing.Verify(password ?? "", user?.PasswordHash ?? DummyHash.Value);
        return ok && user is { Disabled: false } ? user : null;
    }

    /// <summary>Null when <paramref name="userName"/> can be used for a new account, otherwise why not.</summary>
    public string? ValidateNewName(string? userName)
    {
        var name = Normalize(userName);
        if (!NameRegex().IsMatch(name))
            return "Usernames are 1–64 lowercase letters, digits, '.', '_' or '-', starting with a letter or digit.";
        return FindByName(name) is null ? null : $"There's already an account named '{name}'.";
    }

    public WebUser Create(string userName, string? password, bool isAdmin, string? displayName = null, ExternalLogin? login = null)
    {
        if (ValidateNewName(userName) is { } problem) throw new ArgumentException(problem);
        if (password is not null && PasswordHashing.Validate(password) is { } weak) throw new ArgumentException(weak);

        var name = Normalize(userName);
        var id   = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        WebUser user;
        Refresh();
        lock (_lock)
        {
            if (_users.Any(u => u.UserName == name)) throw new ArgumentException($"There's already an account named '{name}'.");
            user = new WebUser
            {
                Id             = id,
                UserName       = name,
                DisplayName    = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
                IsAdmin        = isAdmin || _users.Count == 0,
                PasswordHash   = password is null ? null : PasswordHashing.Hash(password),
                Home           = _users.Count == 0 ? FuseraftHome.Root : Path.Combine(_dataDir, "homes", id),
                WorkspaceRoots = [DefaultRoot(name)],
            };
            if (login is not null) user.Logins.Add(login);
            _users.Add(user);
            SetupCode = null;
            Persist();
        }
        Changed?.Invoke();
        return user;
    }

    /// <summary>Changes an account. A change that should end its sign-ins (see <see cref="WebUser.SecurityStamp"/>) passes <paramref name="signOut"/>.</summary>
    public void Update(string id, Action<WebUser> change, bool signOut = false)
    {
        Refresh();
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id) ?? throw new InvalidOperationException("No such account.");
            var before = (user.IsAdmin, user.Disabled);
            change(user);
            if (before.IsAdmin && !before.Disabled && (!user.IsAdmin || user.Disabled) && !OtherAdminExists(id))
            {
                (user.IsAdmin, user.Disabled) = before;
                throw new InvalidOperationException("That's the last admin; make someone else an admin first.");
            }
            if (signOut) user.SecurityStamp = Guid.NewGuid().ToString("N");
            Persist();
        }
        Changed?.Invoke();
    }

    public void SetPassword(string id, string password)
    {
        if (PasswordHashing.Validate(password) is { } weak) throw new ArgumentException(weak);
        Update(id, u => u.PasswordHash = PasswordHashing.Hash(password), signOut: true);
    }

    /// <summary>Removes an account. Its fuseraft home is left on disk.</summary>
    public void Delete(string id)
    {
        Refresh();
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return;
            if (user is { IsAdmin: true, Disabled: false } && !OtherAdminExists(id))
                throw new InvalidOperationException("That's the last admin; make someone else an admin first.");
            _users.Remove(user);
            Persist();
        }
        Changed?.Invoke();
    }

    public bool CheckSetupCode(string? code) =>
        SetupCode is { } expected && code is not null &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(code.Trim()));

    private bool OtherAdminExists(string id) => _users.Any(u => u.Id != id && u is { IsAdmin: true, Disabled: false });

    private string DefaultRoot(string userName) =>
        WebOptions.ExpandHome(_auth.DefaultWorkspaceRoot.Replace("{data}", _dataDir).Replace("{user}", userName));

    private void Persist()
    {
        Directory.CreateDirectory(_dataDir);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_users, JsonOpts));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, _path, overwrite: true);
        _loadedWriteTime = File.GetLastWriteTimeUtc(_path);
    }

    private static string Normalize(string? userName) => (userName ?? "").Trim().ToLowerInvariant();

    private static string NewCode() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex NameRegex();
}
