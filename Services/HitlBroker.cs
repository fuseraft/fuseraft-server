using System.Collections.Concurrent;
using fuseraft.Web.Models;
using fuseraft.Web.Services.Users;

namespace fuseraft.Web.Services;

/// <summary>
/// Holds every approval a fuseraft process is waiting on, for the HITL page (and the REPL page) to
/// answer. Each request parks its process's <see cref="ApprovalRelay"/> until someone responds. Pages see and
/// answer only what their user may (<see cref="WebUser.CanAccess"/>); the unfiltered members are for the services.
/// </summary>
public sealed class HitlBroker
{
    private readonly ConcurrentDictionary<Guid, PendingApproval> _pending = new();

    public event Action? PendingChanged;

    public IReadOnlyCollection<PendingApproval> GetPending() =>
        _pending.Values.OrderBy(p => p.Created).ToList();

    public IReadOnlyCollection<PendingApproval> GetPending(string sessionId) =>
        _pending.Values.Where(p => p.SessionId == sessionId).OrderBy(p => p.Created).ToList();

    public IReadOnlyCollection<PendingApproval> GetPending(WebUser user) =>
        _pending.Values.Where(p => user.CanAccess(p.OwnerId)).OrderBy(p => p.Created).ToList();

    public IReadOnlyCollection<PendingApproval> GetPending(WebUser user, string sessionId) =>
        GetPending(sessionId).Where(p => user.CanAccess(p.OwnerId)).ToList();

    /// <summary>Answers a request for <paramref name="user"/>; one they may not answer is left waiting.</summary>
    public void Respond(WebUser user, Guid id, bool approved)
    {
        if (_pending.TryGetValue(id, out var req) && user.CanAccess(req.OwnerId))
            Respond(id, approved);
    }

    public PendingApproval Enqueue(PendingApproval request)
    {
        _pending[request.Id] = request;
        PendingChanged?.Invoke();
        return request;
    }

    public void Respond(Guid id, bool approved)
    {
        if (_pending.TryRemove(id, out var req))
        {
            req.Tcs.TrySetResult(approved);
            PendingChanged?.Invoke();
        }
    }

    /// <summary>Denies everything a session is still waiting on — it was cancelled or its process is gone.</summary>
    public void CancelSession(string sessionId)
    {
        var any = false;
        foreach (var (key, req) in _pending)
        {
            if (req.SessionId != sessionId || !_pending.TryRemove(key, out _)) continue;
            req.Tcs.TrySetResult(false);
            any = true;
        }
        if (any) PendingChanged?.Invoke();
    }
}
