using System.Text;
using System.Text.Json;
using fuseraft.Web.Models;

namespace fuseraft.Web.Services.Daemons;

/// <summary>
/// One attach connection to a <c>fuseraft serve</c> daemon. The daemon broadcasts every task's progress to
/// every connection, but sends approval requests and the result only to the connection that dispatched the
/// task. It reads a connection's next task only after that connection's current one finishes, and reads
/// approval answers from the same stream, so this sends one task at a time.
/// </summary>
public sealed class DaemonConnection(DaemonEndpoint endpoint, HitlBroker broker, Func<IDaemonTransport> transportFactory, bool configured)
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
    private string? _awaitingQueued;
    private DaemonTask? _current;

    public Guid Id { get; } = Guid.NewGuid();
    public DaemonEndpoint Endpoint => endpoint;
    /// <summary>From <c>Fuseraft:Daemons</c>, as opposed to added on the page for this server's lifetime.</summary>
    public bool Configured => configured;
    public DaemonState State { get; private set; }
    public string? Error { get; private set; }

    public event Action? Changed;

    public bool CanDispatch
    {
        get { lock (_lock) return State == DaemonState.Connected && _awaitingQueued is null && _current is null; }
    }

    /// <summary>Tasks this server dispatched, newest first.</summary>
    public IReadOnlyList<DaemonTask> Tasks
    {
        get { lock (_lock) return [.. _tasks]; }
    }

    /// <summary>Live progress from every client's tasks, oldest first.</summary>
    public IReadOnlyList<DaemonActivity> Activity
    {
        get { lock (_lock) return [.. _activity]; }
    }

    public async Task ConnectAsync()
    {
        lock (_lock)
        {
            if (State != DaemonState.Disconnected) return;
            State = DaemonState.Connecting;
            Error = null;
        }
        Changed?.Invoke();

        var transport = transportFactory();
        var cts = new CancellationTokenSource();
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            handshake.CancelAfter(HandshakeTimeout);
            await transport.ConnectAsync(new Uri(endpoint.Url), endpoint.Token, handshake.Token);
            var hello = await transport.ReceiveLineAsync(handshake.Token)
                ?? throw new InvalidOperationException("The daemon closed the connection before saying hello.");
            CheckHello(hello);
        }
        catch (Exception ex)
        {
            await transport.DisposeAsync();
            cts.Dispose();
            SetDisconnected(ex is OperationCanceledException ? "The daemon didn't answer in time." : Describe(ex));
            return;
        }

        lock (_lock)
        {
            _transport = transport;
            _cts       = cts;
            _relay     = new ApprovalRelay(broker, new LineWriter(transport), ApprovalSource.Daemon, endpoint.Name);
            State      = DaemonState.Connected;
        }
        AddActivity($"Connected to {endpoint.Url}");
        _ = ReadLoopAsync(transport, cts.Token);
    }

    public async Task DisconnectAsync()
    {
        IDaemonTransport? transport;
        lock (_lock)
        {
            transport = _transport;
            _transport = null;
            _cts?.Cancel();
        }
        if (transport is null) return;
        await transport.DisposeAsync();
        SetDisconnected(null);
    }

    public async Task DispatchAsync(string task)
    {
        IDaemonTransport transport;
        lock (_lock)
        {
            if (State != DaemonState.Connected || _transport is null)
                throw new InvalidOperationException("Not connected to the daemon.");
            if (_awaitingQueued is not null || _current is not null)
                throw new InvalidOperationException("Your last task is still running. The daemon takes one task at a time from each connection.");
            _awaitingQueued = task;
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
        SetDisconnected(error);
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
                lock (_lock)
                {
                    relay     = _relay;
                    sessionId = _current?.SessionId ?? "?";
                }
                relay?.Handle(sessionId, e);
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
        lock (_lock)
        {
            if (_awaitingQueued is not null)
            {
                _current = new DaemonTask { SessionId = sessionId, Task = _awaitingQueued, Position = position };
                _tasks.Insert(0, _current);
                _awaitingQueued = null;
            }
        }
        AddActivity(position is null or 0 ? $"Queued session {sessionId}, running next" : $"Queued session {sessionId}, {position} ahead");
    }

    private void OnResult(JsonElement e)
    {
        var sessionId = Str(e, "sessionId");
        var succeeded = e.TryGetProperty("succeeded", out var s) && s.ValueKind == JsonValueKind.True;
        lock (_lock)
        {
            var task = _tasks.FirstOrDefault(t => t.SessionId == sessionId);
            if (task is not null)
            {
                task.Status       = succeeded ? DaemonTaskStatus.Succeeded : DaemonTaskStatus.Failed;
                task.LastAgent    = Str(e, "lastAgent");
                task.LastMessage  = Str(e, "lastMessage");
                task.ErrorMessage = Str(e, "errorMessage");
            }
            if (_current?.SessionId == sessionId) _current = null;
        }
        AddActivity(succeeded ? $"✓ Session {sessionId} finished" : $"✗ Session {sessionId} failed: {Str(e, "errorMessage")}");
    }

    private void SetDisconnected(string? error)
    {
        string? lostSession = null;
        lock (_lock)
        {
            if (_current is not null)
            {
                _current.Status       = DaemonTaskStatus.Lost;
                _current.ErrorMessage = "The connection closed before the task finished. The daemon may still be running it.";
                lostSession = _current.SessionId;
            }
            _current        = null;
            _awaitingQueued = null;
            _relay          = null;
            _cts?.Dispose();
            _cts  = null;
            // Nobody can answer the daemon any more, so drop the approval cards, before anyone sees it disconnected.
            if (lostSession is not null) broker.CancelSession(lostSession);
            State = DaemonState.Disconnected;
            Error = error;
        }
        AddActivity(error is null ? "Disconnected" : $"Disconnected: {error}");
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

    private static void CheckHello(string line)
    {
        int? version = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (Str(doc.RootElement, "type") == "hello") version = Int(doc.RootElement, "protocolVersion");
        }
        catch (JsonException) { }

        if (version != ProtocolVersion)
            throw new InvalidOperationException(version is null
                ? "This doesn't look like a fuseraft serve attach endpoint, or the daemon's fuseraft is too old (it needs `fuseraft attach --url` support)."
                : $"The daemon speaks protocol version {version}; this server understands version {ProtocolVersion}.");
    }

    private static string Describe(Exception ex) => ex switch
    {
        System.Net.WebSockets.WebSocketException w when w.Message.Contains("'401'") =>
            "The daemon refused the token. Check that it matches the daemon's FUSERAFT_SERVE_TOKEN.",
        System.Net.WebSockets.WebSocketException w when w.Message.Contains("'403'") =>
            "The daemon refused the connection (403).",
        _ => ex.InnerException?.Message is { Length: > 0 } inner ? $"{ex.Message} {inner}" : ex.Message,
    };

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
