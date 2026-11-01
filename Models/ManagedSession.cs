using fuseraft.Web.Services;
using fuseraft.Web.Services.Cli;

namespace fuseraft.Web.Models;

public sealed class ManagedSession
{
    private readonly Lock _syncLock = new();
    private readonly List<SessionEvent> _events = [];

    public required string   SessionId        { get; init; }
    /// <summary>The account that started it, or whose fuseraft home it was found in.</summary>
    public required string   OwnerId          { get; init; }
    public required string   Task             { get; init; }
    public required string   ConfigPath       { get; init; }
    public string?           WorkingDirectory { get; init; }
    public SessionStatus     Status           { get; set; }
    public DateTimeOffset    StartedAt        { get; init; }
    public DateTimeOffset?   EndedAt          { get; set; }
    public bool?             Succeeded        { get; set; }
    public string?           ErrorMessage     { get; set; }

    /// <summary>The <c>run --bridge</c> process driving this session; null when it isn't running.</summary>
    internal IBridgeProcess? Process { get; set; }
    internal ApprovalRelay?  Relay   { get; set; }
    internal bool CancelRequested { get; set; }
    /// <summary>Whether the stored messages of a session from history have been fetched.</summary>
    internal bool HistoryLoaded { get; set; }

    public bool IsLive => Status is SessionStatus.Starting or SessionStatus.Running;

    public void AddEvent(SessionEvent evt)     { lock (_syncLock) _events.Add(evt); }
    public List<SessionEvent> SnapshotEvents() { lock (_syncLock) return [.._events]; }

    public string DurationLabel
    {
        get
        {
            var end = EndedAt ?? (IsLive ? DateTimeOffset.UtcNow : StartedAt);
            var span = end - StartedAt;
            return span.TotalHours >= 1
                ? span.ToString(@"h\:mm\:ss")
                : span.ToString(@"m\:ss");
        }
    }
}

public enum SessionStatus { Idle, Starting, Running, Succeeded, Failed, Cancelled }
