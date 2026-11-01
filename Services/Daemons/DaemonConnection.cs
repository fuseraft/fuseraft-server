using System.Text;
using System.Text.Json;
using fuseraft.Web.Models;

namespace fuseraft.Web.Services.Daemons;

/// <summary>
/// One attach connection to a <c>fuseraft serve</c> daemon. The daemon broadcasts every task's progress to
/// every connection, but sends approval requests and the result only to the client that dispatched the
/// task. It reads a client's next task only after that client's current one finishes, and reads
/// approval answers from the same stream, so this sends one task at a time.
/// <para>
/// The connection sends a client id that stays the same across reconnects and restarts (see <see cref="DaemonStore"/>),
/// so after a dropped connection the daemon asks this server again for the running task's approval, and sends
/// the results that finished meanwhile. Until <see cref="DisconnectAsync"/>, a dropped connection is retried.
/// </para>
/// </summary>
/// <param name="store">Remembers who sent each unfinished task, so its approvals go to them after this server restarts.</param>
public sealed class DaemonConnection(
    DaemonEndpoint endpoint, HitlBroker broker, Func<IDaemonTransport> transportFactory, bool configured,
    string? clientId = null, DaemonStore? store = null)
{
    public const int ProtocolVersion = 1;
    private const int MaxActivity = 200;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private readonly List<DaemonTask> _tasks = [];
    private readonly List<DaemonActivity> _activity = [];
    private IDaemonTransport? _transport;
    private ApprovalRelay? _relay;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _retry;
    private (string Task, string? OwnerId)? _awaitingQueued;
    private DaemonTask? _current;
    private bool _wanted;
    private int _attempt;

    public Guid Id { get; } = Guid.NewGuid();
    public DaemonEndpoint Endpoint => endpoint;
    public string ClientId { get; } = clientId ?? Guid.NewGuid().ToString("N");
    /// <summary>From <c>Fuseraft:Daemons</c>, as opposed to added on the page.</summary>
    public bool Configured => configured;
    public DaemonState State { get; private set; }
    public string? Error { get; private set; }
    /// <summary>
    /// False for a daemon older than fuseraft v1.14.0, which ignores the client id: after a dropped connection it decides
    /// the running task's approval by its unattended policy, and doesn't keep results for this server.
    /// </summary>
    public bool SupportsReconnect { get; private set; } = true;
    /// <summary>This server's token for the daemon is read-only: it can watch, but the daemon refuses its tasks.</summary>
    public bool ReadOnly { get; private set; }
    /// <summary>When the next reconnect attempt is due, while <see cref="State"/> is <see cref="DaemonState.Reconnecting"/>.</summary>
    public DateTimeOffset? NextAttempt { get; private set; }

    /// <summary>The waits between reconnect attempts; the last one repeats.</summary>
    internal TimeSpan[] RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    public event Action? Changed;

    public bool CanDispatch
    {
        get { lock (_lock) return State == DaemonState.Connected && !ReadOnly && _awaitingQueued is null && _current is null; }
    }

    /// <summary>Tasks this server dispatched, newest first.</summary>
    public IReadOnlyList<DaemonTask> Tasks
    {
        get { lock (_lock) return [.. _tasks]; }
    }

    /// <summary>The tasks <paramref name="user"/> may see: their own, or every one for an admin.</summary>
    public IReadOnlyList<DaemonTask> TasksFor(fuseraft.Web.Services.Users.WebUser user) =>
        [.. Tasks.Where(t => user.CanAccess(t.OwnerId))];


    /// <summary>Live progress from every client's tasks, oldest first.</summary>
    public IReadOnlyList<DaemonActivity> Activity
    {
        get { lock (_lock) return [.. _activity]; }
    }

    /// <summary>Connects, and keeps reconnecting after the connection drops until <see cref="DisconnectAsync"/>.</summary>
    public async Task ConnectAsync()
    {
        lock (_lock)
        {
            if (State is DaemonState.Connecting or DaemonState.Connected) return;
            _wanted  = true;
            _attempt = 0;
            CancelRetry();
            State = DaemonState.Connecting;
            Error = null;
        }
        Changed?.Invoke();
        await AttemptAsync();
    }

    private async Task AttemptAsync()
    {
        var transport = transportFactory();
        var cts = new CancellationTokenSource();
        JsonElement hello;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            handshake.CancelAfter(HandshakeTimeout);
            await transport.ConnectAsync(endpoint, ClientId, handshake.Token);
            var line = await transport.ReceiveLineAsync(handshake.Token)
                ?? throw new IOException("The daemon closed the connection before saying hello.");
            hello = CheckHello(line);
        }
        catch (Exception ex)
        {
            await transport.DisposeAsync();
            cts.Dispose();
            var error = ex is OperationCanceledException ? "The daemon didn't answer in time." : Describe(ex);
            // A wrong token, certificate or protocol won't fix itself by trying again.
            ConnectionEnded(error, retry: !IsPermanent(ex));
            return;
        }

        bool abandoned;
        lock (_lock)
        {
            abandoned = !_wanted;
            if (!abandoned)
            {
                _transport = transport;
                _cts       = cts;
                _relay     = new ApprovalRelay(broker, new LineWriter(transport), ApprovalSource.Daemon, endpoint.Name);
                _attempt   = 0;
                State      = DaemonState.Connected;
                Error      = null;
                NextAttempt = null;
                SupportsReconnect = hello.TryGetProperty("clientId", out _);
                ReadOnly          = hello.TryGetProperty("readOnly", out var r) && r.ValueKind == JsonValueKind.True;
                Reconcile(hello);
            }
        }
        if (abandoned)
        {
            // Disconnected while the handshake was under way.
            await transport.DisposeAsync();
            cts.Dispose();
            return;
        }
        AddActivity($"Connected to {endpoint.Url}");
        _ = ReadLoopAsync(transport, cts.Token);
    }

    /// <summary>Disconnects and stops reconnecting. A running task carries on; reconnect to pick it up again.</summary>
    public async Task DisconnectAsync()
    {
        IDaemonTransport? transport;
        lock (_lock)
        {
            _wanted = false;
            CancelRetry();
            transport = _transport;
            _transport = null;
            _cts?.Cancel();
            if (transport is null && State == DaemonState.Disconnected) return;
        }
        if (transport is not null) await transport.DisposeAsync();
        SetDisconnected(null, reconnecting: false);
    }

    /// <param name="ownerId">The account sending it, who alone (with the admins) sees it and answers its approvals.</param>
    public async Task DispatchAsync(string task, string? ownerId = null)
    {
        IDaemonTransport transport;
        lock (_lock)
        {
            if (State != DaemonState.Connected || _transport is null)
                throw new InvalidOperationException("Not connected to the daemon.");
            if (ReadOnly)
                throw new InvalidOperationException("This server's token for the daemon is read-only, so it can't send tasks.");
            if (_awaitingQueued is not null || _current is not null)
                throw new InvalidOperationException((_current?.OwnerId ?? _awaitingQueued?.OwnerId) == ownerId
                    ? "Your last task is still running. The daemon takes one task at a time from each connection."
                    : "Another task sent from this server is still running. The daemon takes one task at a time from each connection.");
            _awaitingQueued = (task, ownerId);
            transport = _transport;
        }
        Changed?.Invoke();

        try
        {
            await transport.SendLineAsync(JsonSerializer.Serialize(new { type = "user_input", text = task }), CancellationToken.None);
        }
        catch
        {
            lock (_lock) _awaitingQueued = null;
            Changed?.Invoke();
            throw;
        }
    }

    private async Task ReadLoopAsync(IDaemonTransport transport, CancellationToken ct)
    {
        string? error = "The daemon closed the connection.";
        try
        {
            while (await transport.ReceiveLineAsync(ct) is { } line)
                Handle(line);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) { error = Describe(ex); }

        lock (_lock)
        {
            if (_transport != transport) return; // DisconnectAsync already tore it down
            _transport = null;
        }
        await transport.DisposeAsync();
        ConnectionEnded(error, retry: true);
    }

    private void ConnectionEnded(string error, bool retry)
    {
        bool reconnecting;
        lock (_lock)
        {
            if (!retry) _wanted = false;
            reconnecting = _wanted;
        }
        SetDisconnected(error, reconnecting);
        if (reconnecting) ScheduleRetry();
    }

    private void ScheduleRetry()
    {
        TimeSpan delay;
        CancellationToken token;
        lock (_lock)
        {
            if (!_wanted) return;
            delay = RetryDelays[Math.Min(_attempt++, RetryDelays.Length - 1)];
            CancelRetry();
            _retry = new CancellationTokenSource();
            token = _retry.Token;
            NextAttempt = DateTimeOffset.UtcNow + delay;
        }
        Changed?.Invoke();
        _ = Task.Delay(delay, token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            lock (_lock)
            {
                if (!_wanted || State != DaemonState.Reconnecting) return;
                State = DaemonState.Connecting;
                NextAttempt = null;
            }
            Changed?.Invoke();
            await AttemptAsync();
        }, TaskScheduler.Default);
    }

    private void CancelRetry()
    {
        _retry?.Cancel();
        _retry?.Dispose();
        _retry = null;
        NextAttempt = null;
    }

    /// <summary>
    /// Matches the daemon's <c>hello</c> against the tasks this server knew of: the one still running carries on, those
    /// whose results are about to arrive wait for them, and one the daemon doesn't mention is gone (it restarted).
    /// A task this server didn't know of, because it restarted itself, is added.
    /// </summary>
    private void Reconcile(JsonElement hello)
    {
        var inFlight = hello.TryGetProperty("inFlight", out var f) && f.ValueKind == JsonValueKind.Object ? f : (JsonElement?)null;
        var inFlightId = inFlight is { } i ? Str(i, "sessionId") : null;
        var pending = hello.TryGetProperty("pendingResults", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet()
            : [];

        foreach (var t in _tasks.Where(t => t.Status is DaemonTaskStatus.Reconnecting or DaemonTaskStatus.Lost))
        {
            if (t.SessionId == inFlightId || pending.Contains(t.SessionId))
            {
                t.Status       = DaemonTaskStatus.Queued;
                t.ErrorMessage = null;
                if (t.SessionId == inFlightId) _current = t;
            }
            else if (t.Status == DaemonTaskStatus.Reconnecting)
            {
                t.Status       = DaemonTaskStatus.Lost;
                t.ErrorMessage = "The daemon no longer has this task; it may have restarted. Its session history has the outcome.";
            }
        }
        if (inFlight is { } running && inFlightId is not null && _current is null)
        {
            _current = new DaemonTask
            {
                SessionId = inFlightId, Task = Str(running, "task") ?? "", Position = Int(running, "position"),
                OwnerId   = store?.TaskOwner(endpoint.Name, inFlightId),
            };
            _tasks.Insert(0, _current);
        }
    }

    private void Handle(string line)
    {
        JsonElement e;
        try
        {
            using var doc = JsonDocument.Parse(line);
            e = doc.RootElement.Clone();
        }
        catch (JsonException) { return; }
        if (e.ValueKind != JsonValueKind.Object) return;

        switch (Str(e, "type"))
        {
            case "queued":
                OnQueued(Str(e, "sessionId") ?? "?", Int(e, "position"));
                break;
            case "auto_dispatched":
                AddActivity($"The daemon started a task from {Str(e, "objectiveId")}: {Str(e, "task")} (session {Str(e, "sessionId")})");
                break;
            case "task_resumed":
                AddActivity($"↻ The daemon restarted and is resuming session {Str(e, "sessionId")}: {Str(e, "task")}");
                break;
            case "a2a_call":
                AddActivity($"A2A call to {Str(e, "agent")} (session {Str(e, "sessionId")})");
                break;
            case "agent_starting":
                AddActivity($"{Str(e, "agent")} is working…");
                break;
            case "tool_calling":
                AddActivity($"{Str(e, "agent")} → {Str(e, "tool")}({Str(e, "argsSummary")})");
                break;
            case "token_budget_warning":
                AddActivity($"{Str(e, "agent")} is near its context budget ({Int(e, "inputTokens")}/{Int(e, "warnThreshold")} tokens)");
                break;
            case "approval_request":
                ApprovalRelay? relay;
                string sessionId;
                string? ownerId;
                lock (_lock)
                {
                    relay     = _relay;
                    sessionId = _current?.SessionId ?? "?";
                    ownerId   = _current?.OwnerId;
                }
                relay?.Handle(sessionId, e, ownerId);
                AddActivity($"Waiting for approval ({Str(e, "kind")}) on session {sessionId}");
                break;
            case "result":
                OnResult(e);
                break;
            case "error":
                lock (_lock) _awaitingQueued = null;
                AddActivity($"Daemon error: {Str(e, "text")}");
                break;
        }
    }

    private void OnQueued(string sessionId, int? position)
    {
        string? owner = null;
        lock (_lock)
        {
            if (_awaitingQueued is { } awaiting)
            {
                _current = new DaemonTask { SessionId = sessionId, Task = awaiting.Task, Position = position, OwnerId = awaiting.OwnerId };
                _tasks.Insert(0, _current);
                _awaitingQueued = null;
                owner = awaiting.OwnerId;
            }
        }
        if (owner is not null) store?.SetTaskOwner(endpoint.Name, sessionId, owner);
        AddActivity(position is null or 0 ? $"Queued session {sessionId}, running next" : $"Queued session {sessionId}, {position} ahead");
    }

    private void OnResult(JsonElement e)
    {
        var sessionId = Str(e, "sessionId") ?? "?";
        var succeeded = e.TryGetProperty("succeeded", out var s) && s.ValueKind == JsonValueKind.True;
        lock (_lock)
        {
            var task = _tasks.FirstOrDefault(t => t.SessionId == sessionId);
            if (task is null)
            {
                // Dispatched before this server restarted; the daemon kept the result for it.
                task = new DaemonTask { SessionId = sessionId, Task = Str(e, "task") ?? "", OwnerId = store?.TaskOwner(endpoint.Name, sessionId) };
                _tasks.Insert(0, task);
            }
            task.Status       = succeeded ? DaemonTaskStatus.Succeeded : DaemonTaskStatus.Failed;
            task.LastAgent    = Str(e, "lastAgent");
            task.LastMessage  = Str(e, "lastMessage");
            task.ErrorMessage = Str(e, "errorMessage");
            if (_current?.SessionId == sessionId) _current = null;
        }
        store?.SetTaskOwner(endpoint.Name, sessionId, null);
        AddActivity(succeeded ? $"✓ Session {sessionId} finished" : $"✗ Session {sessionId} failed: {Str(e, "errorMessage")}");
    }

    private void SetDisconnected(string? error, bool reconnecting)
    {
        var interrupted = new List<string>();
        bool retrying;
        lock (_lock)
        {
            retrying = reconnecting && _attempt > 0;
            foreach (var t in _tasks.Where(t => t.Status is DaemonTaskStatus.Queued or DaemonTaskStatus.Reconnecting))
            {
                t.Status       = reconnecting ? DaemonTaskStatus.Reconnecting : DaemonTaskStatus.Lost;
                t.ErrorMessage = reconnecting
                    ? "Reconnecting to the daemon to pick the task up again."
                    : "Disconnected before the task finished. The daemon may still be running it; connect again to pick it up.";
                interrupted.Add(t.SessionId);
            }
            _current        = null;
            _awaitingQueued = null;
            _relay          = null;
            _cts?.Dispose();
            _cts  = null;
            // Nobody can answer the daemon on this connection any more, so drop the approval cards before anyone
            // sees it disconnected. After a reconnect the daemon asks again.
            foreach (var session in interrupted) broker.CancelSession(session);
            State = reconnecting ? DaemonState.Reconnecting : DaemonState.Disconnected;
            Error = error;
        }
        // One line per outage: a daemon that's down for an hour would otherwise push everything else out of the feed.
        if (retrying) Changed?.Invoke();
        else AddActivity(error is null ? "Disconnected" : reconnecting ? $"Connection lost: {error} Reconnecting…" : $"Disconnected: {error}");
    }

    private void AddActivity(string text)
    {
        lock (_lock)
        {
            _activity.Add(new DaemonActivity(text));
            if (_activity.Count > MaxActivity) _activity.RemoveRange(0, _activity.Count - MaxActivity);
        }
        Changed?.Invoke();
    }

    /// <summary>The <c>hello</c>, once it's known to speak this server's protocol.</summary>
    private static JsonElement CheckHello(string line)
    {
        int? version = null;
        JsonElement hello = default;
        try
        {
            using var doc = JsonDocument.Parse(line);
            hello = doc.RootElement.Clone();
            if (hello.ValueKind == JsonValueKind.Object && Str(hello, "type") == "hello") version = Int(hello, "protocolVersion");
        }
        catch (JsonException) { }

        if (version != ProtocolVersion)
            throw new ProtocolMismatchException(version is null
                ? "This doesn't look like a fuseraft serve attach endpoint, or the daemon's fuseraft is too old (it needs `fuseraft attach --url` support)."
                : $"The daemon speaks protocol version {version}; this server understands version {ProtocolVersion}.");
        return hello;
    }

    private static bool IsPermanent(Exception ex) =>
        ex is ProtocolMismatchException
        || ex is System.Net.WebSockets.WebSocketException w && (w.Message.Contains("'401'") || w.Message.Contains("'403'"))
        || FindInner<System.Security.Authentication.AuthenticationException>(ex) is not null;

    private static string Describe(Exception ex) => ex switch
    {
        System.Net.WebSockets.WebSocketException w when w.Message.Contains("'401'") =>
            "The daemon refused the token. Check that it matches the daemon's FUSERAFT_SERVE_TOKEN.",
        System.Net.WebSockets.WebSocketException w when w.Message.Contains("'403'") =>
            "The daemon refused the connection (403).",
        _ when FindInner<System.Security.Authentication.AuthenticationException>(ex) is not null =>
            "The daemon's TLS certificate isn't trusted. For a self-signed certificate, pin the SHA-256 fingerprint `fuseraft serve` prints.",
        _ => ex.InnerException?.Message is { Length: > 0 } inner ? $"{ex.Message} {inner}" : ex.Message,
    };

    private static T? FindInner<T>(Exception? ex) where T : Exception
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is T match) return match;
        return null;
    }

    private sealed class ProtocolMismatchException(string message) : Exception(message);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    /// <summary>Lets <see cref="ApprovalRelay"/> write its answers to the daemon.</summary>
    private sealed class LineWriter(IDaemonTransport transport) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteLineAsync(string? value) =>
            transport.SendLineAsync(value ?? "", CancellationToken.None);
    }
}
