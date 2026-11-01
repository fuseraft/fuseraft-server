namespace fuseraft.Web.Services.Users;

/// <summary>The <c>Auth</c> section: how people sign in and where new accounts may work.</summary>
public sealed class AuthOptions
{
    /// <summary>Username and password sign-in. Turn off to allow OIDC only.</summary>
    public bool LocalAccounts { get; set; } = true;

    /// <summary>
    /// Where a new non-admin account may work: <c>{data}</c> is <see cref="WebOptions.DataDirectory"/>,
    /// <c>{user}</c> the username. Admins can change each account's roots on the Users page.
    /// </summary>
    public string DefaultWorkspaceRoot { get; set; } = "{data}/workspaces/{user}";

    /// <summary>How long a sign-in lasts without activity.</summary>
    public double SessionHours { get; set; } = 12;

    public OidcOptions Oidc { get; set; } = new();
}

public sealed class OidcOptions
{
    public string? Authority    { get; set; }
    public string? ClientId     { get; set; }
    public string? ClientSecret { get; set; }
    /// <summary>The sign-in button's label.</summary>
    public string  DisplayName  { get; set; } = "single sign-on";
    public List<string> Scopes  { get; set; } = ["openid", "profile", "email"];

    /// <summary>Create an account on first sign-in for an allowed email; otherwise an admin links accounts first.</summary>
    public bool AutoProvision { get; set; }
    /// <summary>Email domains (<c>example.com</c>) whose users may be created on first sign-in.</summary>
    public List<string> AllowedEmailDomains { get; set; } = [];
    /// <summary>Emails that are admins when their account is created.</summary>
    public List<string> AdminEmails { get; set; } = [];

    public bool Enabled => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>Where the server keeps its own state, from <c>Fuseraft:DataDirectory</c>.</summary>
public sealed class WebOptions
{
    public string DataDirectory { get; set; } = "~/.fuseraft-web";

    /// <summary>Tick every user's <c>fuseraft schedule run</c> once a minute, so scheduled jobs run without cron.</summary>
    public bool RunSchedules { get; set; } = true;

    public string ResolvedDataDirectory => ExpandHome(DataDirectory);

    internal static string ExpandHome(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        path = path.Trim();
        if (path == "~") return home;
        if (path.StartsWith("~/") || path.StartsWith("~\\")) path = Path.Combine(home, path[2..]);
        return Path.GetFullPath(path);
    }
}
