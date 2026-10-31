namespace fuseraft.Server.Models;

/// <summary>A running <c>fuseraft serve</c> to attach to, from <c>Fuseraft:Daemons</c> or added on the Daemons page.</summary>
public sealed class DaemonEndpoint
{
    public string  Name  { get; set; } = "";
    /// <summary>The daemon's attach WebSocket, e.g. <c>ws://build-box:24601/attach</c>.</summary>
    public string  Url   { get; set; } = "";
    /// <summary>The daemon's <c>FUSERAFT_SERVE_TOKEN</c>, when it has one.</summary>
    public string? Token { get; set; }
}

public enum DaemonState { Disconnected, Connecting, Connected }

public enum DaemonTaskStatus { Queued, Succeeded, Failed, Lost }

/// <summary>A task this server dispatched to a daemon.</summary>
public sealed class DaemonTask
{
    public required string SessionId { get; init; }
    public required string Task      { get; init; }
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
