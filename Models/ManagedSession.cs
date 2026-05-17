using fuseraft.Core.Models;

namespace fuseraft.Server.Models;

public sealed class ManagedSession
{
    public required string   SessionId    { get; init; }
    public required string   Task         { get; init; }
    public required string   ConfigPath   { get; init; }
    public string            WorkspaceDir { get; init; } = string.Empty;
    public SessionStatus     Status       { get; set; }
    public DateTimeOffset    StartedAt    { get; init; }
    public DateTimeOffset?   EndedAt      { get; set; }
    public bool?             Succeeded    { get; set; }
    public string?           ErrorMessage { get; set; }
    public List<AgentMessage> Messages    { get; init; } = [];
    public List<SessionEvent> Events      { get; init; } = [];

    // Null for sessions loaded from history (not currently running)
    public CancellationTokenSource? Cts { get; set; }

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
