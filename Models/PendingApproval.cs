using System.Text.Json;

namespace fuseraft.Web.Models;

public enum ApprovalSource { Session, Repl, Daemon }

/// <summary>An <c>approval_request</c> from a fuseraft process, waiting for someone to approve or deny it.</summary>
public sealed class PendingApproval
{
    public Guid            Id        { get; init; } = Guid.NewGuid();
    public required string SessionId { get; init; }
    public ApprovalSource  Source    { get; init; }
    /// <summary>The account whose process is asking; null means only admins may answer.</summary>
    public string?         OwnerId   { get; init; }
    /// <summary>For <see cref="ApprovalSource.Daemon"/>: which daemon is asking.</summary>
    public string?         Origin    { get; init; }
    /// <summary>The request's <c>kind</c>; one this server doesn't know is shown raw.</summary>
    public required string Kind      { get; init; }
    public DateTimeOffset  Created   { get; init; } = DateTimeOffset.UtcNow;

    public string? Command     { get; init; }
    public string? Plugin      { get; init; }
    public string? Action      { get; init; }
    public string? Detail      { get; init; }
    public string? Path        { get; init; }
    public string? OldContent  { get; init; }
    public string? NewContent  { get; init; }
    public string? Keyword     { get; init; }
    public string? SourceAgent { get; init; }
    public string? TargetAgent { get; init; }
    public string  RawJson     { get; init; } = "";

    public TaskCompletionSource<bool> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static PendingApproval FromEvent(string sessionId, ApprovalSource source, JsonElement e,
        string? origin = null, string? ownerId = null) => new()
    {
        SessionId   = sessionId,
        OwnerId     = ownerId,
        Source      = source,
        Origin      = origin,
        Kind        = Str(e, "kind") ?? "unknown",
        Command     = Str(e, "command"),
        Plugin      = Str(e, "plugin"),
        Action      = Str(e, "action"),
        Detail      = Str(e, "detail"),
        Path        = Str(e, "path"),
        OldContent  = Str(e, "oldContent"),
        NewContent  = Str(e, "newContent"),
        Keyword     = Str(e, "keyword"),
        SourceAgent = Str(e, "sourceAgent"),
        TargetAgent = Str(e, "targetAgent"),
        RawJson     = e.GetRawText(),
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
