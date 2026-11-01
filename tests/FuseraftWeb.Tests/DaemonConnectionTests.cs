using System.Text.Json;
using System.Threading.Channels;
using fuseraft.Web.Models;
using fuseraft.Web.Services;
using fuseraft.Web.Services.Daemons;

namespace FuseraftWeb.Tests;

public sealed class DaemonConnectionTests
{
    private const string Hello = """{"type":"hello","protocolVersion":1}""";

    private readonly HitlBroker _broker = new();
    private readonly List<FakeTransport> _transports = [new()];
    private int _made;

    /// <summary>The transport the first connection attempt gets.</summary>
    private FakeTransport First => _transports[0];

    /// <summary>The transport the next attempt gets, made ready ahead of it.</summary>
    private FakeTransport Next()
    {
        lock (_transports)
        {
            var t = new FakeTransport();
            _transports.Add(t);
            return t;
        }
    }

    private int Attempts
    {
        get { lock (_transports) return _made; }
    }

    private DaemonConnection Connection(string? token = null, TimeSpan? retry = null, DaemonStore? store = null) =>
        new(new DaemonEndpoint { Name = "box", Url = "ws://box:1/attach", Token = token }, _broker, MakeTransport,
            configured: false, clientId: "client-1", store)
        {
            RetryDelays = [retry ?? TimeSpan.FromHours(1)],
        };

    private FakeTransport MakeTransport()
    {
        lock (_transports)
        {
            if (_made == _transports.Count) _transports.Add(new FakeTransport());
            return _transports[_made++];
        }
    }

    [Fact]
    public async Task Connect_SendsTheTokenAndWaitsForHello()
    {
        var c = Connection("tok");
        First.Push(Hello);

        await c.ConnectAsync();

        Assert.Equal(DaemonState.Connected, c.State);
        Assert.Equal(("ws://box:1/attach", "tok", "client-1"), (First.Url, First.Token, First.ClientId));
    }

    [Theory]
    [InlineData("""{"type":"hello","protocolVersion":2}""", "version 2")]
    [InlineData("""{"type":"hello"}""", "too old")]
    [InlineData("not json", "too old")]
    public async Task Connect_RefusesAnotherProtocol(string hello, string expected)
    {
        var c = Connection();
        First.Push(hello);

        await c.ConnectAsync();

        Assert.Equal(DaemonState.Disconnected, c.State);
        Assert.Contains(expected, c.Error);
        Assert.True(First.Disposed);
    }

    [Fact]
    public async Task Connect_ExplainsARefusedToken()
    {
        First.ConnectError = new System.Net.WebSockets.WebSocketException(
            "The server returned status code '401' when status code '101' was expected.");
        var c = Connection();

        await c.ConnectAsync();

        Assert.Contains("FUSERAFT_SERVE_TOKEN", c.Error);
    }

    [Fact]
    public async Task Dispatch_TracksTheTaskThroughToItsResult()
    {
        var c = await ConnectedAsync();

        await c.DispatchAsync("write a haiku");
        using (var sent = JsonDocument.Parse(First.Sent.Single()))
        {
            Assert.Equal("user_input", sent.RootElement.GetProperty("type").GetString());
            Assert.Equal("write a haiku", sent.RootElement.GetProperty("text").GetString());
        }
        Assert.False(c.CanDispatch);

        First.Push("""{"type":"queued","sessionId":"s1","position":2}""");
        var task = await Eventually(() => c.Tasks.SingleOrDefault());
        Assert.Equal(("s1", "write a haiku", 2), (task.SessionId, task.Task, task.Position));

        First.Push("""{"type":"result","sessionId":"s1","succeeded":true,"lastAgent":"Poet","lastMessage":"done"}""");
        await Eventually(() => c.CanDispatch ? c : null);
        Assert.Equal(DaemonTaskStatus.Succeeded, task.Status);
        Assert.Equal(("Poet", "done"), (task.LastAgent, task.LastMessage));
    }

    [Fact]
    public async Task Dispatch_WhileATaskIsRunning_IsRefused()
    {
        var c = await ConnectedAsync();
        await c.DispatchAsync("first");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => c.DispatchAsync("second"));

        Assert.Contains("one task at a time", refused.Message);
        Assert.Single(First.Sent);
    }

    [Fact]
    public async Task ApprovalRequest_IsAnsweredThroughTheBroker()
    {
        var c = await ConnectedAsync();
        await c.DispatchAsync("rm stuff");
        First.Push("""{"type":"queued","sessionId":"s1","position":0}""");
        First.Push("""{"type":"approval_request","kind":"shell_command","command":"rm -rf build"}""");

        var request = await Eventually(() => _broker.GetPending().SingleOrDefault());
        Assert.Equal((ApprovalSource.Daemon, "box", "s1", "rm -rf build"), (request.Source, request.Origin, request.SessionId, request.Command));

        _broker.Respond(request.Id, approved: true);

        var answer = await Eventually(() => First.Sent.Skip(1).FirstOrDefault());
        Assert.Equal("""{"type":"approval_response","approved":true}""", answer);
    }

    [Fact]
    public async Task LosingTheConnection_DropsItsApprovalsAndReconnects()
    {
        var c = await ConnectedAsync();
        await c.DispatchAsync("deploy");
        First.Push("""{"type":"queued","sessionId":"s1","position":0}""");
        First.Push("""{"type":"approval_request","kind":"shell_command","command":"deploy.sh"}""");
        var request = await Eventually(() => _broker.GetPending().SingleOrDefault());

        First.Close();

        await Eventually(() => c.State == DaemonState.Reconnecting ? c : null);
        Assert.Contains("closed the connection", c.Error);
        Assert.NotNull(c.NextAttempt);
        Assert.Empty(_broker.GetPending());
        Assert.False(await request.Tcs.Task);
        Assert.Equal(DaemonTaskStatus.Reconnecting, c.Tasks.Single().Status);
    }

    [Fact]
    public async Task Reconnecting_PicksUpTheRunningTaskAndAsksItsApprovalAgain()
    {
        var c = await ConnectedAsync(retry: TimeSpan.FromMilliseconds(20));
        await c.DispatchAsync("deploy");
        First.Push("""{"type":"queued","sessionId":"s1","position":0}""");
        await Eventually(() => c.Tasks.SingleOrDefault());
        var second = Next();
        second.Push("""{"type":"hello","protocolVersion":1,"clientId":"client-1","inFlight":{"sessionId":"s1","task":"deploy","status":"running","position":null},"pendingResults":[]}""");
        second.Push("""{"type":"approval_request","kind":"shell_command","command":"deploy.sh"}""");

        First.Close();

        var request = await Eventually(() => _broker.GetPending().SingleOrDefault());
        Assert.Equal(("s1", "deploy.sh"), (request.SessionId, request.Command));
        Assert.Equal(DaemonState.Connected, c.State);
        Assert.Equal("client-1", second.ClientId);
        Assert.Equal(DaemonTaskStatus.Queued, c.Tasks.Single().Status);
        Assert.False(c.CanDispatch);

        _broker.Respond(request.Id, approved: true);
        Assert.Equal("""{"type":"approval_response","approved":true}""", await Eventually(() => second.Sent.FirstOrDefault()));
    }

    [Fact]
    public async Task Reconnecting_TasksTheDaemonNoLongerHasAreLost()
    {
        var c = await ConnectedAsync(retry: TimeSpan.FromMilliseconds(20));
        await c.DispatchAsync("deploy");
        First.Push("""{"type":"queued","sessionId":"s1","position":0}""");
        await Eventually(() => c.Tasks.SingleOrDefault());
        Next().Push(Hello);

        First.Close();

        await Eventually(() => c.State == DaemonState.Connected && c.Tasks.Single().Status == DaemonTaskStatus.Lost ? c : null);
        Assert.Contains("restarted", c.Tasks.Single().ErrorMessage);
        Assert.True(c.CanDispatch);
    }

    [Fact]
    public async Task AfterARestart_TheDaemonsRecordsOfThisClientsTasksComeBack()
    {
        var c = Connection();
        First.Push("""{"type":"hello","protocolVersion":1,"clientId":"client-1","inFlight":{"sessionId":"s2","task":"still going","status":"queued","position":1},"pendingResults":["s1"]}""");
        First.Push("""{"type":"result","sessionId":"s1","task":"done while away","succeeded":true,"lastAgent":"Dev","lastMessage":"ok"}""");

        await c.ConnectAsync();

        var finished = await Eventually(() => c.Tasks.FirstOrDefault(t => t.SessionId == "s1"));
        Assert.Equal(("done while away", DaemonTaskStatus.Succeeded), (finished.Task, finished.Status));
        var running = c.Tasks.Single(t => t.SessionId == "s2");
        Assert.Equal(("still going", DaemonTaskStatus.Queued, 1), (running.Task, running.Status, running.Position));
        Assert.False(c.CanDispatch);
    }

    [Fact]
    public async Task AFailedAttempt_IsRetried()
    {
        First.ConnectError = new System.Net.WebSockets.WebSocketException("Unable to connect to the remote server");
        Next().Push(Hello);
        var c = Connection(retry: TimeSpan.FromMilliseconds(20));

        await c.ConnectAsync();

        await Eventually(() => c.State == DaemonState.Connected ? c : null);
        Assert.Equal(2, Attempts);
    }

    [Fact]
    public async Task AnOutage_IsOneLineOfActivity_HoweverManyAttemptsFail()
    {
        var c = await ConnectedAsync(retry: TimeSpan.FromMilliseconds(10));
        for (var i = 0; i < 5; i++)
            Next().ConnectError = new System.Net.WebSockets.WebSocketException("Unable to connect to the remote server");
        Next().Push(Hello);

        First.Close();

        await Eventually(() => c.State == DaemonState.Connected && Attempts == 7 ? c : null);
        Assert.Single(c.Activity, a => a.Text.StartsWith("Connection lost"));
    }

    [Fact]
    public async Task ARefusedToken_IsNotRetried()
    {
        First.ConnectError = new System.Net.WebSockets.WebSocketException(
            "The server returned status code '401' when status code '101' was expected.");
        var c = Connection(retry: TimeSpan.FromMilliseconds(20));

        await c.ConnectAsync();
        await Task.Delay(200);

        Assert.Equal((DaemonState.Disconnected, 1), (c.State, Attempts));
    }

    [Fact]
    public async Task Disconnect_StopsReconnecting()
    {
        // Long enough that polling can't miss the Reconnecting state, which ends when the retry starts.
        var retry = TimeSpan.FromSeconds(1);
        var c = await ConnectedAsync(retry);
        First.Close();
        await Eventually(() => c.State == DaemonState.Reconnecting ? c : null);

        await c.DisconnectAsync();
        await Task.Delay(retry * 1.5);

        Assert.Equal((DaemonState.Disconnected, 1), (c.State, Attempts));
    }

    [Fact]
    public async Task Broadcasts_FromOtherClientsShowAsActivity()
    {
        var c = await ConnectedAsync();

        First.Push("""{"type":"auto_dispatched","sessionId":"s9","objectiveId":"OBJ-0001","task":"fix lint"}""");
        First.Push("""{"type":"tool_calling","agent":"Dev","tool":"shell_run","argsSummary":"npm test"}""");

        await Eventually(() => c.Activity.Any(a => a.Text.Contains("npm test")) ? c : null);
        Assert.Contains(c.Activity, a => a.Text.Contains("OBJ-0001"));
        Assert.Empty(c.Tasks);
    }

    [Fact]
    public async Task Disconnect_ClosesTheTransport()
    {
        var c = await ConnectedAsync();

        await c.DisconnectAsync();

        Assert.Equal(DaemonState.Disconnected, c.State);
        Assert.Null(c.Error);
        Assert.True(First.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.DispatchAsync("x"));
    }

    [Fact]
    public async Task ATasksApprovals_GoToWhoeverSentIt()
    {
        var c = await ConnectedAsync();
        await c.DispatchAsync("deploy", ownerId: "alice");
        First.Push("""{"type":"queued","sessionId":"s1","position":0}""");
        First.Push("""{"type":"approval_request","kind":"shell_command","command":"deploy.sh"}""");

        var request = await Eventually(() => _broker.GetPending().SingleOrDefault());

        Assert.Equal("alice", request.OwnerId);
        Assert.Single(c.TasksFor(User("alice")));
        Assert.Empty(c.TasksFor(User("bob")));
        Assert.Single(c.TasksFor(User("root", admin: true)));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => c.DispatchAsync("mine too", ownerId: "bob"));
        Assert.Contains("Another task", refused.Message);
    }

    [Fact]
    public async Task WhoSentATask_SurvivesARestartOfThisServer()
    {
        var dir = Directory.CreateTempSubdirectory("fuseraft-web-owners-").FullName;
        try
        {
            var store = new DaemonStore(Path.Combine(dir, "daemons.json"), new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
            var first = Connection(store: store);
            First.Push(Hello);
            await first.ConnectAsync();
            await first.DispatchAsync("deploy", ownerId: "alice");
            First.Push("""{"type":"queued","sessionId":"s1","position":0}""");
            await Eventually(() => store.TaskOwner("box", "s1"));

            var second = Connection(store: store);
            var transport = Next();
            transport.Push("""{"type":"hello","protocolVersion":1,"clientId":"client-1","inFlight":{"sessionId":"s1","task":"deploy","status":"running"},"pendingResults":[]}""");
            transport.Push("""{"type":"approval_request","kind":"shell_command","command":"deploy.sh"}""");
            await second.ConnectAsync();

            Assert.Equal("alice", (await Eventually(() => _broker.GetPending().SingleOrDefault())).OwnerId);
            transport.Push("""{"type":"result","sessionId":"s1","succeeded":true}""");
            await Eventually(() => second.Tasks.Single().Status == DaemonTaskStatus.Succeeded ? second : null);
            Assert.Null(store.TaskOwner("box", "s1"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("""{"type":"hello","protocolVersion":1}""", false)]
    [InlineData("""{"type":"hello","protocolVersion":1,"clientId":"client-1","inFlight":null,"pendingResults":[]}""", true)]
    public async Task ADaemonThatIgnoresTheClientId_IsFlagged(string hello, bool supported)
    {
        var c = Connection();
        First.Push(hello);

        await c.ConnectAsync();

        Assert.Equal(supported, c.SupportsReconnect);
    }

    [Fact]
    public async Task AReadOnlyToken_WatchesButDoesntDispatch()
    {
        var c = Connection();
        First.Push("""{"type":"hello","protocolVersion":1,"clientId":"client-1","readOnly":true,"inFlight":null,"pendingResults":[]}""");
        await c.ConnectAsync();

        Assert.True(c.ReadOnly);
        Assert.False(c.CanDispatch);
        Assert.Contains("read-only", (await Assert.ThrowsAsync<InvalidOperationException>(() => c.DispatchAsync("deploy"))).Message);
        Assert.Empty(First.Sent);
    }

    [Fact]
    public async Task AResumedTask_ShowsInTheActivity()
    {
        var c = await ConnectedAsync();

        First.Push("""{"type":"task_resumed","sessionId":"s1","task":"deploy"}""");

        await Eventually(() => c.Activity.Any(a => a.Text.Contains("resuming session s1")) ? c : null);
    }

    private static fuseraft.Web.Services.Users.WebUser User(string id, bool admin = false) =>
        new() { Id = id, UserName = id, Home = "/tmp", IsAdmin = admin };

    private async Task<DaemonConnection> ConnectedAsync(TimeSpan? retry = null)
    {
        var c = Connection(retry: retry);
        First.Push(Hello);
        await c.ConnectAsync();
        Assert.Equal(DaemonState.Connected, c.State);
        return c;
    }

    private static async Task<T> Eventually<T>(Func<T?> probe) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (probe() is { } value) return value;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition never held");
            await Task.Delay(10);
        }
    }

    private sealed class FakeTransport : IDaemonTransport
    {
        private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();
        private readonly Lock _lock = new();
        private readonly List<string> _sent = [];

        public string? Url { get; private set; }
        public string? Token { get; private set; }
        public string? ClientId { get; private set; }
        public Exception? ConnectError { get; set; }
        public bool Disposed { get; private set; }

        public IReadOnlyList<string> Sent
        {
            get { lock (_lock) return [.. _sent]; }
        }

        public void Push(string line) => _incoming.Writer.TryWrite(line);

        public void Close() => _incoming.Writer.TryWrite(null);

        public Task ConnectAsync(DaemonEndpoint endpoint, string clientId, CancellationToken cancellationToken)
        {
            (Url, Token, ClientId) = (endpoint.Url, endpoint.Token, clientId);
            return ConnectError is null ? Task.CompletedTask : Task.FromException(ConnectError);
        }

        public Task SendLineAsync(string line, CancellationToken cancellationToken)
        {
            if (Disposed) return Task.FromException(new ObjectDisposedException(nameof(FakeTransport)));
            lock (_lock) _sent.Add(line);
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveLineAsync(CancellationToken cancellationToken) =>
            await _incoming.Reader.ReadAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
