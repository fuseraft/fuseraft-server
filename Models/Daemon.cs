namespace fuseraft.Web.Models;

/// <summary>A running <c>fuseraft serve</c> to attach to, from <c>Fuseraft:Daemons</c> or added on the Daemons page.</summary>
public sealed class DaemonEndpoint
{
    public string  Name  { get; set; } = "";
    /// <summary>The daemon's attach WebSocket, e.g. <c>ws://build-box:24601/attach</c>.</summary>
    public string  Url   { get; set; } = "";
    /// <summary>The daemon's <c>FUSERAFT_SERVE_TOKEN</c>, when it has one.</summary>
    public string? Token { get; set; }
    /// <summary>
    /// Trust a <c>wss://</c> daemon by its certificate's SHA-256 fingerprint (what <c>fuseraft serve --tls-cert</c>
    /// prints) instead of by a CA, for a self-signed certificate.
    /// </summary>
    public string? CertificateSha256 { get; set; }
    /// <summary>Connect when the server starts, and keep reconnecting after the connection drops.</summary>
    public bool AutoConnect { get; set; } = true;
    /// <summary>
    /// The non-admin accounts, by username, who may send this daemon tasks and answer their approvals. Admins always
    /// may. Each sees only their own tasks, but the activity feed shows every task's progress.
    /// </summary>
    public List<string> Users { get; set; } = [];

    public bool Allows(fuseraft.Web.Services.Users.WebUser user) =>
        user.IsAdmin || Users.Contains(user.UserName, StringComparer.OrdinalIgnoreCase);

    /// <summary>A <c>ws://</c> URL to anything but this machine: the token and every task travel unencrypted.</summary>
    public bool Unencrypted =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme == "ws" && !uri.IsLoopback;
}

public enum DaemonState { Disconnected, Connecting, Connected, Reconnecting }

public enum DaemonTaskStatus { Queued, Succeeded, Failed, Lost, Reconnecting }

/// <summary>A task this server dispatched to a daemon.</summary>
public sealed class DaemonTask
{
    public required string SessionId { get; init; }
    public required string Task      { get; init; }
    /// <summary>The account that sent it; null when that isn't known (sent before this server kept track).</summary>
    public string?         OwnerId   { get; init; }
    public DateTimeOffset  Created   { get; init; } = DateTimeOffset.UtcNow;
    public DaemonTaskStatus Status   { get; set; } = DaemonTaskStatus.Queued;
    /// <summary>How many tasks were ahead of it when it was queued.</summary>
    public int?    Position     { get; set; }
    public string? LastAgent    { get; set; }
    public string? LastMessage  { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>One line of a daemon's live progress, from any client's task.</summary>
public sealed record DaemonActivity(string Text, DateTimeOffset At)
{
    public DaemonActivity(string text) : this(text, DateTimeOffset.UtcNow) { }
}
