using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Server.Models;
using fuseraft.Server.Services;
using fuseraft.Server.Services.Cli;

namespace FuseraftServer.Tests;

public sealed class SessionHostServiceTests
{
    private readonly FakeCli    _cli    = new();
    private readonly HitlBroker _broker = new();

    private const string EmptyHistory = """{"protocolVersion":1,"items":[]}""";

    private SessionHostService Host(string history = EmptyHistory)
    {
        _cli.Responses["sessions --all --json"] = history;
        return new SessionHostService(_cli, _broker, NullLogger<SessionHostService>.Instance);
    }

    private static object Ready(string id = "abcd1234", int version = FuseraftCli.ProtocolVersion) =>
        new { type = "ready", protocolVersion = version, sessionId = id, task = "Build it", config = "/w/o.yaml", resumed = false, agents = new[] { "Dev" } };

    private static object Result(bool succeeded, string? errorMessage = null) =>
        new { type = "result", sessionId = "abcd1234", succeeded, errorMessage, exitCode = succeeded ? 0 : 1 };

    private static object Message(string agent, string content) => new
    {
        type = "agent_message",
        message = new { agentName = agent, role = "assistant", content, turnIndex = 0, toolCalls = new[] { new { name = "write_file", argsSummary = "path=a", succeeded = true } } },
        elapsedMs = 1500,
    };

    [Fact]
    public async Task Start_StreamsMessagesAndFinishes()
    {
        var bridge = new FakeBridge().Send(Ready());
        _cli.Bridges.Enqueue(bridge);
        var host = Host();

        var id = await host.StartSessionAsync("Build it", "/w/o.yaml", "/w");

        Assert.Equal("abcd1234", id);
        Assert.Equal(["run", "--bridge", "-c", "/w/o.yaml", "Build it"], _cli.Started[0].Args);
        Assert.Equal("/w", _cli.Started[0].Cwd);

        bridge.Send(new { type = "agent_starting", agent = "Dev" })
              .Send(new { type = "tool_calling", agent = "Dev", tool = "write_file", argsSummary = "path=a" })
              .SendRaw("not json — ignored")
              .Send(Message("Dev", "done"))
              .Send(Result(true));
        bridge.Exit(0);

        var session = host.GetSession(id)!;
        await Wait.UntilAsync(() => session.Status == SessionStatus.Succeeded, "the session to finish");
        var events = session.SnapshotEvents();
        Assert.Equal(["session_start", "agent_starting", "tool_calling", "message", "session_end"], events.Select(e => e.Type));
        Assert.Equal("write_file(path=a)", events[2].Detail);
        Assert.Equal("done", events[3].Message!.Content);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), events[3].Elapsed);
    }

    [Fact]
    public async Task ResultBeforeReady_IsTheStartError()
    {
        _cli.Bridges.Enqueue(new FakeBridge().Send(Result(false, "Config error: not found")));
        var host = Host();

        var ex = await Assert.ThrowsAsync<FuseraftCliException>(() => host.StartSessionAsync("t", "/w/o.yaml"));

        Assert.Contains("Config error", ex.Message);
        Assert.Empty(host.GetSessions());
    }

    [Fact]
    public async Task OtherProtocolVersion_RefusesAndKills()
    {
        var bridge = new FakeBridge().Send(Ready(version: 2));
        _cli.Bridges.Enqueue(bridge);
        var host = Host();

        var ex = await Assert.ThrowsAsync<FuseraftCliException>(() => host.StartSessionAsync("t", "/w/o.yaml"));

        Assert.Contains("protocol version 2", ex.Message);
        Assert.True(bridge.Killed);
    }

    [Fact]
    public async Task ExitBeforeReady_ReportsStderr()
    {
        var bridge = new FakeBridge { StderrTail = "boom\nUnhandled: missing key" };
        bridge.Exit(134);
        _cli.Bridges.Enqueue(bridge);
        var host = Host();

        var ex = await Assert.ThrowsAsync<FuseraftCliException>(() => host.StartSessionAsync("t", "/w/o.yaml"));

        Assert.Contains("exit 134", ex.Message);
        Assert.Contains("missing key", ex.Message);
    }

    [Fact]
    public async Task ApprovalRequest_IsAnsweredThroughTheBroker()
    {
        var bridge = new FakeBridge().Send(Ready());
        _cli.Bridges.Enqueue(bridge);
        var host = Host();
        var id = await host.StartSessionAsync("t", "/w/o.yaml");

        bridge.Send(new { type = "approval_request", kind = "shell_command", command = "make test" });
        await Wait.UntilAsync(() => _broker.GetPending(id).Count == 1, "the approval to reach the broker");
        var pending = _broker.GetPending(id).Single();
        Assert.Equal("make test", pending.Command);
        Assert.Equal(ApprovalSource.Session, pending.Source);

        _broker.Respond(pending.Id, true);
        await Wait.UntilAsync(() => bridge.InputLines.Length == 1, "the answer");
        Assert.Equal("""{"type":"approval_response","approved":true}""", bridge.InputLines[0]);
    }

    [Fact]
    public async Task ExitWithoutResult_IsAFailureWithStderr()
    {
        var bridge = new FakeBridge().Send(Ready());
        _cli.Bridges.Enqueue(bridge);
        var host = Host();
        var id = await host.StartSessionAsync("t", "/w/o.yaml");

        bridge.Send(new { type = "approval_request", kind = "shell_command", command = "ls" });
        await Wait.UntilAsync(() => _broker.GetPending(id).Count == 1, "the approval");
        bridge.StderrTail = "Segmentation fault";
        bridge.Exit(139);

        var session = host.GetSession(id)!;
        await Wait.UntilAsync(() => session.Status == SessionStatus.Failed, "the failure");
        Assert.Contains("exited unexpectedly (exit 139)", session.ErrorMessage);
        Assert.Contains("Segmentation fault", session.ErrorMessage);
        Assert.Empty(_broker.GetPending(id)); // a dead process's approvals are cleared
    }

    [Fact]
    public async Task Cancel_InterruptsAndEndsCancelled()
    {
        var bridge = new FakeBridge().Send(Ready());
        _cli.Bridges.Enqueue(bridge);
        var host = Host();
        var id = await host.StartSessionAsync("t", "/w/o.yaml");

        host.CancelSession(id);
        await Wait.UntilAsync(() => bridge.InputLines.Contains("""{"type":"interrupt"}"""), "the interrupt");
        bridge.Send(Result(false, "Cancelled.")).Exit(1);

        var session = host.GetSession(id)!;
        await Wait.UntilAsync(() => session.Status == SessionStatus.Cancelled, "the cancel");
        Assert.Equal(SessionStatus.Cancelled, session.Status);
    }

    [Fact]
    public async Task History_ListsPastSessions_AndLoadsMessagesOnDemand()
    {
        var host = Host("""{"protocolVersion":1,"items":[{"sessionId":"aaaa0001","task":"old","workingDirectory":"/w","isComplete":true,"startedAt":"2026-10-01T00:00:00Z","lastUpdatedAt":"2026-10-01T01:00:00Z"}]}""");
        await host.HistoryLoad;
        _cli.Responses["sessions --show aaaa0001 --json"] =
            """{"protocolVersion":1,"item":{"sessionId":"aaaa0001","task":"old","isComplete":true,"messages":[{"agentName":"Dev","role":"assistant","content":"done"}]}}""";

        var listed = Assert.Single(host.GetSessions());
        Assert.Equal(SessionStatus.Succeeded, listed.Status);
        Assert.Empty(listed.SnapshotEvents());

        var loaded = await host.LoadSessionAsync("aaaa0001");

        Assert.Same(listed, loaded);
        Assert.Equal(["message", "session_end"], loaded!.SnapshotEvents().Select(e => e.Type));
        await host.LoadSessionAsync("aaaa0001");
        Assert.Equal(2, loaded.SnapshotEvents().Count); // loaded once
    }

    [Fact]
    public async Task UnknownSession_IsNull()
    {
        var host = Host();
        await host.HistoryLoad;

        Assert.Null(await host.LoadSessionAsync("deadbeef"));
    }

    [Fact]
    public async Task HistoryFailure_IsReportedNotThrown()
    {
        var host = new SessionHostService(_cli, _broker, NullLogger<SessionHostService>.Instance); // no canned list
        await host.HistoryLoad;

        Assert.Contains("no canned response", host.HistoryError);
    }

    [Fact]
    public async Task Resume_RunsInTheSessionsDirectory_AndKeepsItsHistory()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var host = Host($$"""{"protocolVersion":1,"items":[{"sessionId":"abcd1234","task":"t","workingDirectory":"{{dir}}","isComplete":false}]}""");
            await host.HistoryLoad;
            _cli.Responses["sessions --show abcd1234 --json"] =
                """{"protocolVersion":1,"item":{"sessionId":"abcd1234","task":"t","isComplete":false,"messages":[{"agentName":"Dev","content":"halfway"}]}}""";
            _cli.Bridges.Enqueue(new FakeBridge().Send(Ready()));

            await host.ResumeSessionAsync("abcd1234");

            Assert.Equal(["run", "--bridge", "--resume", "abcd1234"], _cli.Started[0].Args);
            Assert.Equal(dir, _cli.Started[0].Cwd);
            var events = host.GetSession("abcd1234")!.SnapshotEvents();
            Assert.Equal("halfway", events[0].Message!.Content);
            Assert.Equal("session_start", events[^1].Type);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Delete_RunsTheCli_AndRefusesALiveSession()
    {
        var bridge = new FakeBridge().Send(Ready());
        _cli.Bridges.Enqueue(bridge);
        var host = Host();
        var id = await host.StartSessionAsync("t", "/w/o.yaml");

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.DeleteSessionAsync(id));

        bridge.Send(Result(true)).Exit(0);
        await Wait.UntilAsync(() => !host.GetSession(id)!.IsLive, "the session to end");
        await host.DeleteSessionAsync(id);

        Assert.Equal(["sessions", "--delete", id], _cli.Runs.Single());
        Assert.Null(host.GetSession(id));
    }
}
