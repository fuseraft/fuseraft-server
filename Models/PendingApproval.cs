namespace fuseraft.Server.Models;

public sealed class PendingApproval
{
    public Guid            Id        { get; init; }
    public required string SessionId { get; init; }
    public HitlRequestType Type      { get; init; }
    public required string Prompt    { get; init; }
    public DateTimeOffset  Created   { get; init; } = DateTimeOffset.UtcNow;

    public required TaskCompletionSource<HitlResponse> Tcs { get; init; }
}

public record HitlResponse(HitlAction Action, string? Message = null);

public enum HitlRequestType { Continue, Redirect, RouteApproval, PostSession, ShellApproval, PlanReview }
public enum HitlAction       { Continue, Approve, Abort, Respond }
