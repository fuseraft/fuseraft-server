using System.Collections.Concurrent;
using fuseraft.Server.Models;

namespace fuseraft.Server.Services;

/// <summary>
/// Mediates between the running orchestrator (which calls IHumanApprovalService methods)
/// and the Blazor HITL page (which renders pending items and calls Respond).
/// Each approval request parks the orchestrator's async call via TaskCompletionSource
/// until the operator responds in the browser.
/// </summary>
public sealed class HitlBroker
{
    private readonly ConcurrentDictionary<Guid, PendingApproval> _pending = new();

    public event Action? PendingChanged;

    public IReadOnlyCollection<PendingApproval> GetPending() =>
        _pending.Values.OrderBy(p => p.Created).ToList();

    public PendingApproval Enqueue(string sessionId, HitlRequestType type, string prompt)
    {
        var req = new PendingApproval
        {
            Id        = Guid.NewGuid(),
            SessionId = sessionId,
            Type      = type,
            Prompt    = prompt,
            Tcs       = new TaskCompletionSource<HitlResponse>(
                            TaskCreationOptions.RunContinuationsAsynchronously),
        };
        _pending[req.Id] = req;
        PendingChanged?.Invoke();
        return req;
    }

    public void Respond(Guid id, HitlResponse response)
    {
        if (_pending.TryRemove(id, out var req))
        {
            req.Tcs.TrySetResult(response);
            PendingChanged?.Invoke();
        }
    }

    public void CancelSession(string sessionId)
    {
        foreach (var (key, req) in _pending)
        {
            if (req.SessionId != sessionId) continue;
            if (_pending.TryRemove(key, out _))
                req.Tcs.TrySetResult(new HitlResponse(HitlAction.Abort));
        }
        PendingChanged?.Invoke();
    }
}
