using System.Collections.Concurrent;
using fuseraft.Server.Models;

namespace fuseraft.Server.Services;

/// <summary>
/// Holds every approval a fuseraft process is waiting on, for the HITL page (and the REPL page) to
/// answer. Each request parks its process's <see cref="ApprovalRelay"/> until someone responds.
/// </summary>
public sealed class HitlBroker
{
    private readonly ConcurrentDictionary<Guid, PendingApproval> _pending = new();

    public event Action? PendingChanged;

    public IReadOnlyCollection<PendingApproval> GetPending() =>
        _pending.Values.OrderBy(p => p.Created).ToList();

    public IReadOnlyCollection<PendingApproval> GetPending(string sessionId) =>
        _pending.Values.Where(p => p.SessionId == sessionId).OrderBy(p => p.Created).ToList();

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
