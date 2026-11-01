using fuseraft.Web.Services.Relay;

namespace FuseraftWeb.Tests;

/// <summary>A relay (in-process) and its server-side bridge, over a real Unix socket.</summary>
public sealed class RelayTests : IAsyncDisposable
{
    private readonly string _dir    = Directory.CreateTempSubdirectory("fuseraft-relay").FullName;
    private readonly string _socket = Path.Combine(Path.GetTempPath(), $"fwr-{Guid.NewGuid():N}"[..20] + ".sock");
    private readonly FakeBridge _child = new();
    private RelayHost? _host;
    private Task<int>? _run;

    public async ValueTask DisposeAsync()
    {
        _child.Exit(0);
        if (_host is not null) await _host.DisposeAsync();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private async Task<RelayBridge> StartAsync()
    {
        _host = RelayHost.Start(new Child(_child), Path.Combine(_dir, "log"), _socket);
        _run  = _host.RunToExitAsync();
        return (await RelayBridge.ConnectAsync(_dir, _socket, TimeSpan.FromSeconds(5)))!;
    }

    private static async Task<string> NextAsync(RelayBridge bridge) =>
        await bridge.Output.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) ?? "<end>";

    [Fact]
    public async Task Lines_FlowBothWays()
    {
        var bridge = await StartAsync();

        _child.Send(new { type = "ready" });
        Assert.Contains("ready", await NextAsync(bridge));

        bridge.Input.WriteLine("""{"type":"user_input","text":"hi"}""");
        await Wait.UntilAsync(() => _child.InputLines.Length == 1, "the input to reach the child");
        Assert.Equal("""{"type":"user_input","text":"hi"}""", _child.InputLines[0]);
    }

    [Fact]
    public async Task ServerLeaving_LeavesTheProcessRunning_AndTheNextServerGetsEverything()
    {
        var first = await StartAsync();
        _child.Send(new { type = "ready", sessionId = "s1" });
        _child.Send(new { type = "approval_request", kind = "shell_command", command = "ls" });
        await NextAsync(first);
        await NextAsync(first);
        first.Input.WriteLine("""{"type":"approval_response","approved":true}""");
        first.Input.WriteLine("""{"type":"user_input","text":"now deploy"}""");
        await Wait.UntilAsync(() => _child.InputLines.Length == 2, "both inputs");

        first.Dispose(); // the server stops
        Assert.False(first.WaitForExitAsync().IsCompleted);
        Assert.False(_child.Killed);

        // While no server is connected, the session carries on and asks again.
        _child.Send(new { type = "agent_message", text = "working" });
        _child.Send(new { type = "approval_request", kind = "shell_command", command = "deploy.sh" });

        var second = (await RelayBridge.ConnectAsync(_dir, _socket, TimeSpan.FromSeconds(5)))!;
        List<string> seen = [];
        for (var i = 0; i < 4; i++) seen.Add(await NextAsync(second));

        Assert.Contains("\"ready\"", seen[0]);
        Assert.Contains("replayed_input", seen[1]);  // the answered ls request is gone
        Assert.Contains("now deploy", seen[1]);
        Assert.Contains("working", seen[2]);
        Assert.Contains("deploy.sh", seen[3]);       // still open, so asked again
        Assert.DoesNotContain(seen, l => l.Contains("\"ls\""));

        // Answering it reaches the process, and the exit comes through.
        second.Input.WriteLine("""{"type":"approval_response","approved":false}""");
        await Wait.UntilAsync(() => _child.InputLines.Length == 3, "the second answer");
        _child.Send(new { type = "result", succeeded = true }).Exit(0);
        Assert.Contains("result", await NextAsync(second));
        Assert.Equal(0, await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, await _run!);

        second.Dispose();
        Assert.False(Directory.Exists(_dir)); // done, so its files go
    }

    [Fact]
    public async Task Kill_EndsTheProcess_AndLettingGoAfterwardsRemovesItsFiles()
    {
        var bridge = await StartAsync();

        bridge.Kill();

        await Wait.UntilAsync(() => _child.Killed, "the kill");
        Assert.Equal(-1, await bridge.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        await _run!;
        bridge.Dispose();
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public async Task Stderr_IsKeptForExplainingAnExit()
    {
        var host = RelayHost.Start(new Child(_child, "boom\nUnhandled: missing key\n"), Path.Combine(_dir, "log"), _socket);
        _host = host;
        _run  = host.RunToExitAsync();
        var bridge = (await RelayBridge.ConnectAsync(_dir, _socket, TimeSpan.FromSeconds(5)))!;

        _child.Exit(134);

        Assert.Equal(134, await bridge.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("missing key", bridge.StderrTail);
    }

    [Fact]
    public void Replay_DropsAnsweredRequests_InOrder_AndSurfacesTyping()
    {
        string[] backlog =
        [
            """o {"type":"ready"}""",
            """o {"type":"approval_request","command":"a"}""",
            """o {"type":"approval_request","command":"b"}""",
            """i {"type":"approval_response","approved":true}""",
            """i {"type":"interrupt"}""",
            """i /clear""",
            """e some stderr""",
            """o {"type":"approval_request","command":"c"}""",
        ];

        var replay = RelayBridge.Replay(backlog).ToList();

        Assert.DoesNotContain(replay, l => l.Contains("\"a\""));   // answered first
        Assert.Contains(replay, l => l.Contains("\"b\""));         // still open
        Assert.Contains(replay, l => l.Contains("\"c\""));
        Assert.DoesNotContain(replay, l => l.Contains("interrupt") || l.Contains("approval_response"));
        Assert.Contains(replay, l => l.Contains("replayed_input") && l.Contains("/clear") && l.Contains("\"raw\":true"));
        Assert.Contains("e some stderr", replay);
    }

    private sealed class Child(FakeBridge bridge, string stderr = "") : IRelayChild
    {
        public TextReader Output => bridge.Output;
        public TextReader Error  { get; } = new StringReader(stderr);
        public TextWriter Input  => bridge.Input;
        public Task<int> WaitForExitAsync() => bridge.WaitForExitAsync();
        public void Kill() => bridge.Kill();
    }
}
