using fuseraft.Core.Models;

namespace fuseraft.Server.Models;

public sealed class ManagedSession
{
    private readonly object _syncLock = new();
    private readonly List<AgentMessage> _messages = [];
    private readonly List<SessionEvent> _events   = [];

    public required string   SessionId    { get; init; }
    public required string   Task         { get; init; }
    public required string   ConfigPath   { get; init; }
    public string            WorkspaceDir { get; init; } = string.Empty;
    public SessionStatus     Status       { get; set; }
    public DateTimeOffset    StartedAt    { get; init; }
    public DateTimeOffset?   EndedAt      { get; set; }
    public bool?             Succeeded    { get; set; }
    public string?           ErrorMessage { get; set; }

    // Null for sessions loaded from history (not currently running)
    public CancellationTokenSource? Cts { get; set; }

    // Pre-existing messages to seed the checkpoint with when resuming a session
    public IReadOnlyList<AgentMessage> ResumeMessages { get; init; } = [];

    public void AddEvent(SessionEvent evt)                          { lock (_syncLock) _events.Add(evt); }
    public void AddMessage(AgentMessage msg)                        { lock (_syncLock) _messages.Add(msg); }
    public void AddMessages(IEnumerable<AgentMessage> msgs)         { lock (_syncLock) _messages.AddRange(msgs); }
    public List<SessionEvent> SnapshotEvents()                      { lock (_syncLock) return [.._events]; }

    public string DurationLabel
    {
        get
        {
            var end = EndedAt ?? (Status is SessionStatus.Running or SessionStatus.Starting
                ? DateTimeOffset.UtcNow
                : StartedAt);
            var span = end - StartedAt;
            return span.TotalHours >= 1
                ? span.ToString(@"h\:mm\:ss")
                : span.ToString(@"m\:ss");
        }
    }
}

public enum SessionStatus { Idle, Starting, Running, Succeeded, Failed, Cancelled }
