namespace fuseraft.Server.Services;

/// <summary>Where the server keeps its own files: fuseraft's home directory, resolved the way fuseraft does.</summary>
public static class FuseraftHome
{
    public static string Root
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Environment.GetEnvironmentVariable("FUSERAFT_HOME")?.Trim();
            if (string.IsNullOrEmpty(path)) return Path.Combine(home, ".fuseraft");
            if (path == "~") return home;
            if (path.StartsWith("~/")) path = Path.Combine(home, path[2..]);
            return Path.GetFullPath(path);
        }
    }
}
