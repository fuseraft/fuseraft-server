namespace fuseraft.Web.Services.Users;

/// <summary>
/// Keeps a non-admin's paths inside their <see cref="WebUser.WorkspaceRoots"/>. This bounds what the UI lets
/// them pick and launch in; it isn't a sandbox for the agents, whose tools run as the server's OS user.
/// </summary>
public sealed class WorkspaceAccess
{
    private readonly Func<WebUser?> _user;

    public WorkspaceAccess(CurrentUser current) => _user = () => current.User;

    private WorkspaceAccess(Func<WebUser?> user) => _user = user;

    /// <summary>For tests and tools that act for no particular user.</summary>
    public static WorkspaceAccess Unrestricted { get; } = new(() => null);

    public static WorkspaceAccess For(WebUser user) => new(() => user);

    /// <summary>The roots this user may work in; empty means anywhere.</summary>
    public IReadOnlyList<string> Roots =>
        _user() is { IsAdmin: false } u ? [.. u.WorkspaceRoots.Select(Path.GetFullPath)] : [];

    /// <summary>Where a picker or a blank workspace field starts.</summary>
    public string DefaultDirectory
    {
        get
        {
            if (Roots is [var first, ..])
            {
                Directory.CreateDirectory(first);
                return first;
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    public bool Allows(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var roots = Roots;
        if (roots.Count == 0) return true;
        string full;
        try { full = Path.GetFullPath(path.Trim()); }
        catch { return false; }
        return roots.Any(r => IsUnder(full, r));
    }

    /// <summary>The full path of <paramref name="path"/> (relative to <paramref name="baseDir"/>), or throws if it's off limits.</summary>
    public string Check(string? path, string? baseDir = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.");
        var full = Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(baseDir)
            ? Path.GetFullPath(path.Trim())
            : Path.GetFullPath(Path.Combine(baseDir, path.Trim()));
        if (!Allows(full))
            throw new UnauthorizedAccessException(
                $"'{full}' is outside your workspaces ({string.Join(", ", Roots)}). Ask an admin to add it.");
        return full;
    }

    /// <summary>Whether a picker standing at <paramref name="path"/> may step up to its parent.</summary>
    public bool CanLeave(string path) =>
        Directory.GetParent(path) is { } parent && Allows(parent.FullName);

    private static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(root, comparison) ||
               path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}
