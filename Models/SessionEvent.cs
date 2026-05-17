using fuseraft.Core.Models;

namespace fuseraft.Server.Models;

public sealed record SessionEvent
{
    public string         Type        { get; init; } = string.Empty;
    public AgentMessage?  Message     { get; init; }
    public string?        AgentName   { get; init; }
    public bool?          Succeeded   { get; init; }
    public string?        ErrorMessage { get; init; }
    public DateTimeOffset Timestamp   { get; init; } = DateTimeOffset.UtcNow;
    public TimeSpan?      Elapsed     { get; init; }
}
