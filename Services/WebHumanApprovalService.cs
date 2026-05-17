using fuseraft.Core.Interfaces;
using fuseraft.Server.Models;

namespace fuseraft.Server.Services;

/// <summary>
/// IHumanApprovalService implementation for the web server context.
/// Each call enqueues a pending approval item visible in the HITL page and
/// awaits the operator's browser response before unblocking the orchestrator.
/// </summary>
public sealed class WebHumanApprovalService(HitlBroker broker, string sessionId)
    : IHumanApprovalService
{
    public async Task<string?> PromptContinueAsync()
    {
        var req  = broker.Enqueue(sessionId, HitlRequestType.Continue,
                       "Agent turn complete. Continue or send a redirect message.");
        var resp = await req.Tcs.Task;
        return resp.Action switch
        {
            HitlAction.Abort   => "\x00",
            HitlAction.Respond => resp.Message,
            _                  => null,
        };
    }

    public async Task<string?> PromptRedirectAsync(string agentName)
    {
        var req  = broker.Enqueue(sessionId, HitlRequestType.Redirect,
                       $"Agent '{agentName}' is stuck. Provide a redirect message or abort the session.");
        var resp = await req.Tcs.Task;
        return resp.Action == HitlAction.Abort ? null : resp.Message;
    }

    public async Task<bool> PromptRouteApprovalAsync(string keyword, string sourceAgent, string targetAgent)
    {
        var req  = broker.Enqueue(sessionId, HitlRequestType.RouteApproval,
                       $"Approve handoff: {sourceAgent} → {targetAgent} (trigger: '{keyword}')?");
        var resp = await req.Tcs.Task;
        return resp.Action == HitlAction.Approve;
    }

    public async Task<string?> PromptPostSessionAsync()
    {
        var req  = broker.Enqueue(sessionId, HitlRequestType.PostSession,
                       "Session complete. Send a follow-up message to continue, or end the session.");
        var resp = await req.Tcs.Task;
        return resp.Action switch
        {
            HitlAction.Abort   => "\x00",
            HitlAction.Respond => resp.Message,
            _                  => null,
        };
    }

    public async Task<bool> PromptShellCommandAsync(string command)
    {
        var req  = broker.Enqueue(sessionId, HitlRequestType.ShellApproval,
                       $"Allow shell command?\n\n`{command}`");
        var resp = await req.Tcs.Task;
        return resp.Action == HitlAction.Approve;
    }

    public async Task<string?> PromptPlanReviewAsync(string planText)
    {
        var req  = broker.Enqueue(sessionId, HitlRequestType.PlanReview,
                       $"Review this plan:\n\n{planText}");
        var resp = await req.Tcs.Task;
        return resp.Action == HitlAction.Approve ? null : resp.Message;
    }
}
