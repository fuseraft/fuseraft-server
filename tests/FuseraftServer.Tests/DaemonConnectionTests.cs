using System.Text.Json;
using System.Threading.Channels;
using fuseraft.Server.Models;
using fuseraft.Server.Services;
using fuseraft.Server.Services.Daemons;

namespace FuseraftServer.Tests;

public sealed class DaemonConnectionTests
{
    private const string Hello = """{"type":"hello","protocolVersion":1}""";

    private readonly HitlBroker _broker = new();
    private readonly FakeTransport _transport = new();

    private DaemonConnection Connection(string? token = null) =>
        new(new DaemonEndpoint { Name = "box", Url = "ws://box:1/attach", Token = token }, _broker, () => _transport, configured: false);

    [Fact]
    public async Task Connect_SendsTheTokenAndWaitsForHello()
    {
        var c = Connection("tok");
        _transport.Push(Hello);

        await c.ConnectAsync();

        Assert.Equal(DaemonState.Connected, c.State);
        Assert.Equal(("ws://box:1/attach", "tok"), (_transport.Url!.ToString(), _transport.Token));
    }

    [Theory]
    [InlineData("""{"type":"hello","protocolVersion":2}""", "version 2")]
    [InlineData("""{"type":"hello"}""", "too old")]
    [InlineData("not json", "too old")]
    public async Task Connect_RefusesAnotherProtocol(string hello, string expected)
    {
        var c = Connection();
        _transport.Push(hello);

        await c.ConnectAsync();

        Assert.Equal(DaemonState.Disconnected, c.State);
        Assert.Contains(expected, c.Error);
        Assert.True(_transport.Disposed);
    }

    [Fact]
    public async Task Connect_ExplainsARefusedToken()
    {
        _transport.ConnectError = new System.Net.WebSockets.WebSocketException(
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
        using (var sent = JsonDocument.Parse(_transport.Sent.Single()))
        {
            Assert.Equal("user_input", sent.RootElement.GetProperty("type").GetString());
            Assert.Equal("write a haiku", sent.RootElement.GetProperty("text").GetString());
        }
        Assert.False(c.CanDispatch);

        _transport.Push("""{"type":"queued","sessionId":"s1","position":2}""");
        var task = await Eventually(() => c.Tasks.SingleOrDefault());
        Assert.Equal(("s1", "write a haiku", 2), (task.SessionId, task.Task, task.Position));

        _transport.Push("""{"type":"result","sessionId":"s1","succeeded":true,"lastAgent":"Poet","lastMessage":"done"}""");
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
        Assert.Single(_transport.Sent);
    }

    [Fact]
    public async Task ApprovalRequest_IsAnsweredThroughTheBroker()
    {
        var c = await ConnectedAsync();
        await c.DispatchAsync("rm stuff");
        _transport.Push("""{"type":"queued","sessionId":"s1","position":0}""");
        _transport.Push("""{"type":"approval_request","kind":"shell_command","command":"rm -rf build"}""");

        var request = await Eventually(() => _broker.GetPending().SingleOrDefault());
        Assert.Equal((ApprovalSource.Daemon, "box", "s1", "rm -rf build"), (request.Source, request.Origin, request.SessionId, request.Command));

        _broker.Respond(request.Id, approved: true);

        var answer = await Eventually(() => _transport.Sent.Skip(1).FirstOrDefault());
        Assert.Equal("""{"type":"approval_response","approved":true}""", answer);
    }

    [Fact]
    public async Task LosingTheConnection_DropsItsApprovalsAndMarksTheTask()
    {
        var c = await ConnectedAsync();
        await c.DispatchAsync("deploy");
        _transport.Push("""{"type":"queued","sessionId":"s1","position":0}""");
        _transport.Push("""{"type":"approval_request","kind":"shell_command","command":"deploy.sh"}""");
        var request = await Eventually(() => _broker.GetPending().SingleOrDefault());

        _transport.Close();

        await Eventually(() => c.State == DaemonState.Disconnected ? c : null);
        Assert.Contains("closed the connection", c.Error);
        Assert.Empty(_broker.GetPending());
        Assert.False(await request.Tcs.Task);
        Assert.Equal(DaemonTaskStatus.Lost, c.Tasks.Single().Status);
    }

    [Fact]
    public async Task Broadcasts_FromOtherClientsShowAsActivity()
    {
        var c = await ConnectedAsync();

        _transport.Push("""{"type":"auto_dispatched","sessionId":"s9","objectiveId":"OBJ-0001","task":"fix lint"}""");
        _transport.Push("""{"type":"tool_calling","agent":"Dev","tool":"shell_run","argsSummary":"npm test"}""");

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
        Assert.True(_transport.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.DispatchAsync("x"));
    }

    private async Task<DaemonConnection> ConnectedAsync()
    {
        var c = Connection();
        _transport.Push(Hello);
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

        public Uri? Url { get; private set; }
        public string? Token { get; private set; }
        public Exception? ConnectError { get; set; }
        public bool Disposed { get; private set; }

        public IReadOnlyList<string> Sent
        {
            get { lock (_lock) return [.. _sent]; }
        }

        public void Push(string line) => _incoming.Writer.TryWrite(line);

        public void Close() => _incoming.Writer.TryWrite(null);

        public Task ConnectAsync(Uri url, string? token, CancellationToken cancellationToken)
        {
            (Url, Token) = (url, token);
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
