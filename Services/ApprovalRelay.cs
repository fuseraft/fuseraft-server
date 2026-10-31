using System.Text.Json;
using fuseraft.Server.Models;

namespace fuseraft.Server.Services;

/// <summary>
/// Answers one fuseraft process's <c>approval_request</c> events through the <see cref="HitlBroker"/>.
/// The protocol has no request IDs — each <c>approval_response</c> answers the oldest open request — so
/// answers are written in the order the requests arrived, whatever order people respond in.
/// </summary>
public sealed class ApprovalRelay(HitlBroker broker, TextWriter input, ApprovalSource source)
{
    private readonly Lock _gate = new();
    private Task _last = Task.CompletedTask;

    public PendingApproval Handle(string sessionId, JsonElement approvalRequest)
    {
        var request = broker.Enqueue(PendingApproval.FromEvent(sessionId, source, approvalRequest));
        lock (_gate) _last = RespondInOrderAsync(_last, request);
        return request;
    }

    /// <summary>Completes once every answer so far has been written.</summary>
    public Task Drained
    {
        get { lock (_gate) return _last; }
    }

    /// <summary>
    /// Sends <c>interrupt</c> once every answer so far is written — deny what's pending first, then call
    /// this — so the CLI never reads an interrupt ahead of the answers it's waiting on.
    /// </summary>
    public async Task InterruptAsync()
    {
        await Drained;
        try { await input.WriteLineAsync("""{"type":"interrupt"}"""); } catch { /* process already gone */ }
    }

    private async Task RespondInOrderAsync(Task previous, PendingApproval request)
    {
        await previous;
        var approved = await request.Tcs.Task;
        var line = JsonSerializer.Serialize(new { type = "approval_response", approved });
        try { await input.WriteLineAsync(line); } catch { /* process already gone */ }
    }
}
