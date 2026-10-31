namespace fuseraft.Server.Models;

// The server's side of fuseraft's wire protocol (docs/protocol.md in fuseraft-cli). These mirror the
// JSON, not fuseraft's internal types, so a fuseraft refactor can't break the server's build.

public sealed record WireSession
{
    public string    SessionId        { get; init; } = "";
    public string    Task             { get; init; } = "";
    public string?   WorkingDirectory { get; init; }
    public string?   ConfigPath       { get; init; }
    public DateTime  StartedAt        { get; init; }
    public DateTime  LastUpdatedAt    { get; init; }
    public bool      IsComplete       { get; init; }
    public int       TurnCount        { get; init; }
}

public sealed record WireSessionDetail
{
    public string                 SessionId        { get; init; } = "";
    public string                 Task             { get; init; } = "";
    public string?                WorkingDirectory { get; init; }
    public string                 ConfigPath       { get; init; } = "";
    public DateTime               StartedAt        { get; init; }
    public DateTime               LastUpdatedAt    { get; init; }
    public bool                   IsComplete       { get; init; }
    public List<WireAgentMessage> Messages         { get; init; } = [];
}

public sealed record WireAgentMessage
{
    public string             AgentName           { get; init; } = "";
    public string             Role                { get; init; } = "assistant";
    public string             Content             { get; init; } = "";
    public int                TurnIndex           { get; init; }
    public DateTime           Timestamp           { get; init; }
    public int?               InputTokens         { get; init; }
    public int?               OutputTokens        { get; init; }
    public bool               IsCompactionSummary { get; init; }
    public List<WireToolCall> ToolCalls           { get; init; } = [];

    /// <summary>A routing correction the orchestrator injected, not something an agent or human said.</summary>
    public bool IsOrchestratorCorrection => AgentName == "Orchestrator";
}

public sealed record WireToolCall
{
    public string  Name        { get; init; } = "";
    public string? ArgsSummary { get; init; }
    public bool    Succeeded   { get; init; }
}

public sealed record WireReplSession
{
    public string   SessionId        { get; init; } = "";
    public string   ModelId          { get; init; } = "";
    public string   Cwd              { get; init; } = "";
    public string?  Title            { get; init; }
    public string?  AutoTitle        { get; init; }
    public DateTime StartedAt        { get; init; }
    public DateTime LastUpdatedAt    { get; init; }
    public int      TurnIndex        { get; init; }
    public string   FirstUserMessage { get; init; } = "";

    public string DisplayName => Title ?? AutoTitle ?? FirstUserMessage;
}

public sealed record WireContextItem
{
    public string                Name        { get; init; } = "";
    public string?               Description { get; init; }
    public string                SourcePath  { get; init; } = "";
    public DateTime              ImportedAt  { get; init; }
    public long                  SizeBytes   { get; init; }
    public List<WireContextFile> Files       { get; init; } = [];
}

public sealed record WireContextFile
{
    public string RelativePath { get; init; } = "";
    public long   SizeBytes    { get; init; }
}

public sealed record WireScheduledJob
{
    public string          Name        { get; init; } = "";
    public string?         Description { get; init; }
    public string          Cron        { get; init; } = "";
    public string          Task        { get; init; } = "";
    public string?         Config      { get; init; }
    public string?         WorkDir     { get; init; }
    public bool            Enabled     { get; init; }
    public bool            Due         { get; init; }
    public DateTimeOffset  CreatedAt   { get; init; }
    public DateTimeOffset? LastRun     { get; init; }
    public DateTimeOffset? NextRun     { get; init; }
}

public sealed record WireMemory
{
    public string Guid        { get; init; } = "";
    public string Name        { get; init; } = "";
    public string Type        { get; init; } = "";
    public string Description { get; init; } = "";
    public string Body        { get; init; } = "";
}

public sealed record WireMemoryAgent
{
    public string Agent { get; init; } = "";
    public int    Count { get; init; }
}

public sealed record WireConfigSummary
{
    public string       Path          { get; init; } = "";
    public string?      Name          { get; init; }
    public string?      Description   { get; init; }
    public List<string> Agents        { get; init; } = [];
    public string?      SelectionType { get; init; }
    public string?      Error         { get; init; }
}

public sealed record WireValidation
{
    public string             Path   { get; init; } = "";
    public bool               Valid  { get; init; }
    public List<WireIssue>    Issues { get; init; } = [];
    public WireConfigSummary? Config { get; init; }
}

public sealed record WireIssue
{
    public string Level   { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed record WireTemplate
{
    public string Key         { get; init; } = "";
    public string Label       { get; init; } = "";
    public string Description { get; init; } = "";
}

public sealed record WireInitResult
{
    public string       Template   { get; init; } = "";
    public string       Model      { get; init; } = "";
    public string?      Endpoint   { get; init; }
    public string       ConfigPath { get; init; } = "";
    public List<string> AgentFiles { get; init; } = [];
}

public sealed record WirePlugin
{
    public string             Name      { get; init; } = "";
    public string             Source    { get; init; } = "";
    public List<WireFunction> Functions { get; init; } = [];
}

public sealed record WireFunction
{
    public string Name        { get; init; } = "";
    public string Description { get; init; } = "";
}
