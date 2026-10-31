namespace fuseraft.Server.Models;

public sealed record SessionEvent
{
    public string            Type         { get; init; } = string.Empty;
    public WireAgentMessage? Message      { get; init; }
    public string?           AgentName    { get; init; }
    /// <summary>For <c>tool_calling</c>: the tool and its argument summary.</summary>
    public string?           Detail       { get; init; }
    public bool?             Succeeded    { get; init; }
    public string?           ErrorMessage { get; init; }
    public DateTimeOffset    Timestamp    { get; init; } = DateTimeOffset.UtcNow;
    public TimeSpan?         Elapsed      { get; init; }
}
