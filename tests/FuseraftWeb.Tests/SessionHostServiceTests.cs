using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Web.Models;
using fuseraft.Web.Services;
using fuseraft.Web.Services.Cli;
using fuseraft.Web.Services.Users;

namespace FuseraftWeb.Tests;

public sealed class SessionHostServiceTests : IDisposable
{
    private readonly FakeCli    _cli    = new();
    private readonly HitlBroker _broker = new();
    private readonly Accounts   _accounts = new();
    private WebUser Alice => _accounts.Alice;

    private const string EmptyHistory = """{"protocolVersion":1,"items":[]}""";

    public void Dispose() => _accounts.Dispose();

    private SessionHostService Host(string? history = EmptyHistory)
    {
        if (history is not null) _cli.Responses["sessions --all --json"] = history;
        return new SessionHostService(_cli, _broker, _accounts.Store, new ModelProfileRegistry { UseKeychain = false }, NullLogger<SessionHostService>.Instance);
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

        var id = await host.StartSessionAsync(Alice, "Build it", "/w/o.yaml", "/w");

        Assert.Equal("abcd1234", id);
        Assert.Equal(["run", "--bridge", "-c", "/w/o.yaml", "Build it"], _cli.Started[0].Args);
        Assert.Equal("/w", _cli.Started[0].Cwd);
        Assert.Equal([Alice.Home], _cli.Homes.Distinct());

        bridge.Send(new { type = "agent_starting", agent = "Dev" })
              .Send(new { type = "tool_calling", agent = "Dev", tool = "write_file", argsSummary = "path=a" })
              .SendRaw("not json — ignored")
              .Send(Message("Dev", "done"))
              .Send(Result(true));
        bridge.Exit(0);

        var session = host.GetSession(Alice, id)!;
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

        var ex = await Assert.ThrowsAsync<FuseraftCliException>(() => host.StartSessionAsync(Alice, "t", "/w/o.yaml"));

        Assert.Contains("Config error", ex.Message);
        Assert.Empty(host.GetSessions(Alice));
    }

    [Fact]
    public async Task OtherProtocolVersion_RefusesAndKills()
    {
        var bridge = new FakeBridge().Send(Ready(version: 2));
        _cli.Bridges.Enqueue(bridge);
        var host = Host();

        var ex = await Assert.ThrowsAsync<FuseraftCliException>(() => host.StartSessionAsync(Alice, "t", "/w/o.yaml"));

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

        var ex = await Assert.ThrowsAsync<FuseraftCliException>(() => host.StartSessionAsync(Alice, "t", "/w/o.yaml"));

        Assert.Contains("exit 134", ex.Message);
        Assert.Contains("missing key", ex.Message);
    }

    [Fact]
    public async Task ApprovalRequest_IsAnsweredThroughTheBroker()
    {
        var bridge = new FakeBridge().Send(Ready());
        _cli.Bridges.Enqueue(bridge);
        var host = Host();
        var id = await host.StartSessionAsync(Alice, "t", "/w/o.yaml");

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
        var id = await host.StartSessionAsync(Alice, "t", "/w/o.yaml");

        bridge.Send(new { type = "approval_request", kind = "shell_command", command = "ls" });
        await Wait.UntilAsync(() => _broker.GetPending(id).Count == 1, "the approval");
        bridge.StderrTail = "Segmentation fault";
        bridge.Exit(139);

        var session = host.GetSession(Alice, id)!;
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
        var id = await host.StartSessionAsync(Alice, "t", "/w/o.yaml");

        host.CancelSession(Alice, id);
        await Wait.UntilAsync(() => bridge.InputLines.Contains("""{"type":"interrupt"}"""), "the interrupt");
        bridge.Send(Result(false, "Cancelled.")).Exit(1);

        var session = host.GetSession(Alice, id)!;
        await Wait.UntilAsync(() => session.Status == SessionStatus.Cancelled, "the cancel");
        Assert.Equal(SessionStatus.Cancelled, session.Status);
    }

    [Fact]
    public async Task History_ListsPastSessions_AndLoadsMessagesOnDemand()
    {
        var host = Host("""{"protocolVersion":1,"items":[{"sessionId":"aaaa0001","task":"old","workingDirectory":"/w","isComplete":true,"startedAt":"2026-10-01T00:00:00Z","lastUpdatedAt":"2026-10-01T01:00:00Z"}]}""");
        await host.EnsureHistoryAsync(Alice);
        _cli.Responses["sessions --show aaaa0001 --json"] =
            """{"protocolVersion":1,"item":{"sessionId":"aaaa0001","task":"old","isComplete":true,"messages":[{"agentName":"Dev","role":"assistant","content":"done"}]}}""";

        var listed = Assert.Single(host.GetSessions(Alice));
        Assert.Equal(SessionStatus.Succeeded, listed.Status);
        Assert.Empty(listed.SnapshotEvents());

        var loaded = await host.LoadSessionAsync(Alice, "aaaa0001");

        Assert.Same(listed, loaded);
        Assert.Equal(["message", "session_end"], loaded!.SnapshotEvents().Select(e => e.Type));
        await host.LoadSessionAsync(Alice, "aaaa0001");
        Assert.Equal(2, loaded.SnapshotEvents().Count); // loaded once
    }

    [Fact]
    public async Task UnknownSession_IsNull()
    {
        var host = Host();
        await host.EnsureHistoryAsync(Alice);

        Assert.Null(await host.LoadSessionAsync(Alice, "deadbeef"));
    }

    [Fact]
    public async Task HistoryFailure_IsReportedNotThrown()
    {
        var host = Host(history: null); // no canned list
        await host.EnsureHistoryAsync(Alice);

        Assert.Contains("no canned response", host.HistoryError(Alice));
    }

    [Fact]
    public async Task Resume_RunsInTheSessionsDirectory_AndKeepsItsHistory()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var host = Host($$"""{"protocolVersion":1,"items":[{"sessionId":"abcd1234","task":"t","workingDirectory":"{{dir}}","isComplete":false}]}""");
            await host.EnsureHistoryAsync(Alice);
            _cli.Responses["sessions --show abcd1234 --json"] =
                """{"protocolVersion":1,"item":{"sessionId":"abcd1234","task":"t","isComplete":false,"messages":[{"agentName":"Dev","content":"halfway"}]}}""";
            _cli.Bridges.Enqueue(new FakeBridge().Send(Ready()));

            await host.ResumeSessionAsync(Alice, "abcd1234");

            Assert.Equal(["run", "--bridge", "--resume", "abcd1234"], _cli.Started[0].Args);
            Assert.Equal(dir, _cli.Started[0].Cwd);
            var events = host.GetSession(Alice, "abcd1234")!.SnapshotEvents();
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
        var id = await host.StartSessionAsync(Alice, "t", "/w/o.yaml");

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.DeleteSessionAsync(Alice, id));

        bridge.Send(Result(true)).Exit(0);
        await Wait.UntilAsync(() => !host.GetSession(Alice, id)!.IsLive, "the session to end");
        await host.DeleteSessionAsync(Alice, id);

        Assert.Equal(["sessions", "--delete", id], _cli.Runs.Single());
        Assert.Null(host.GetSession(Alice, id));
    }

    [Fact]
    public async Task Sessions_AreVisibleToTheirOwnerAndAdmins_Only()
    {
        _cli.Bridges.Enqueue(new FakeBridge().Send(Ready()));
        var host = Host();
        var bob  = _accounts.Add("bob", roots: ["/w"]);
        var carol = _accounts.Add("carol", roots: ["/w"]);
        var id = await host.StartSessionAsync(bob, "t", "/w/o.yaml", "/w");

        Assert.Single(host.GetSessions(bob));
        Assert.Single(host.GetSessions(Alice)); // the first account is an admin
        Assert.Empty(host.GetSessions(carol));
        Assert.Null(host.GetSession(carol, id));
        Assert.Null(await host.LoadSessionAsync(carol, id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.DeleteSessionAsync(carol, id));
    }

    [Fact]
    public async Task Approvals_AreAnsweredOnlyByTheirOwnerOrAnAdmin()
    {
        var bridge = new FakeBridge().Send(Ready());
        _cli.Bridges.Enqueue(bridge);
        var host  = Host();
        var bob   = _accounts.Add("bob", roots: ["/w"]);
        var carol = _accounts.Add("carol", roots: ["/w"]);
        var id = await host.StartSessionAsync(bob, "t", "/w/o.yaml", "/w");

        bridge.Send(new { type = "approval_request", kind = "shell_command", command = "rm -rf build" });
        await Wait.UntilAsync(() => _broker.GetPending(id).Count == 1, "the approval");
        var pending = _broker.GetPending(id).Single();
        Assert.Empty(_broker.GetPending(carol));

        _broker.Respond(carol, pending.Id, true);
        Assert.Single(_broker.GetPending(bob)); // carol's answer was ignored

        _broker.Respond(bob, pending.Id, false);
        await Wait.UntilAsync(() => bridge.InputLines.Length == 1, "the answer");
        Assert.Equal("""{"type":"approval_response","approved":false}""", bridge.InputLines[0]);
    }

    [Fact]
    public async Task NonAdmin_CantStartOutsideTheirWorkspaces()
    {
        var host = Host();
        var bob  = _accounts.Add("bob", roots: ["/srv/bob"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.StartSessionAsync(bob, "t", "/etc/o.yaml", "/srv/bob"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.StartSessionAsync(bob, "t", "o.yaml", "/srv/bobby"));
        await Assert.ThrowsAsync<ArgumentException>(() => host.StartSessionAsync(bob, "t", "/srv/bob/o.yaml"));
        Assert.Empty(_cli.Started);
    }

    [Fact]
    public async Task Start_HandsTheOwnersProfileKeysToTheProcessOnly()
    {
        var profiles = new ModelProfileRegistry { UseKeychain = false };
        await profiles.For(Alice).SaveAsync(new fuseraft.Web.Models.ModelProfile { Id = "p1", Name = "x", ModelId = "m" }, "sk-alice");
        _cli.Bridges.Enqueue(new FakeBridge().Send(Ready()));
        _cli.Responses["sessions --all --json"] = EmptyHistory;
        var host = new SessionHostService(_cli, _broker, _accounts.Store, profiles, NullLogger<SessionHostService>.Instance);

        await host.StartSessionAsync(Alice, "t", "/w/o.yaml", "/w");

        Assert.Equal("sk-alice", _cli.Environments[0]!["FUSERAFT_PROFILE_p1_KEY"]);
        Assert.Null(Environment.GetEnvironmentVariable("FUSERAFT_PROFILE_p1_KEY"));
    }

    [Fact]
    public async Task RunningSession_IsAdopted_WithItsOpenApprovalAskedAgain()
    {
        var bob = _accounts.Add("bob", roots: ["/w"]);
        var host = Host();
        var bridge = new FakeBridge()
            .Send(Ready())
            .Send(Message("Dev", "halfway"))
            .Send(new { type = "approval_request", kind = "shell_command", command = "make deploy" });
        var tag = new fuseraft.Web.Services.Relay.BridgeTag
        {
            Kind = SessionHostService.Kind, OwnerId = bob.Id, StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            Extra = { ["cwd"] = "/w" },
        };

        await host.AdoptRunningAsync(tag, bridge);

        var session = host.GetSession(bob, "abcd1234")!;
        Assert.Equal(SessionStatus.Running, session.Status);
        Assert.Equal("/w", session.WorkingDirectory);
        Assert.Equal(tag.StartedAt, session.StartedAt);
        await Wait.UntilAsync(() => _broker.GetPending(bob).Count == 1, "the open approval");
        Assert.Contains(session.SnapshotEvents(), e => e.Message?.Content == "halfway");

        _broker.Respond(bob, _broker.GetPending(bob).Single().Id, true);
        await Wait.UntilAsync(() => bridge.InputLines.Length == 1, "the answer");
    }

    [Fact]
    public async Task RunningSession_OfARemovedAccount_IsEnded()
    {
        var host = Host();
        var bridge = new FakeBridge().Send(Ready());

        await host.AdoptRunningAsync(new fuseraft.Web.Services.Relay.BridgeTag { Kind = SessionHostService.Kind, OwnerId = "gone" }, bridge);

        Assert.True(bridge.Killed);
        Assert.Empty(host.GetSessions(Alice));
    }

    [Fact]
    public async Task StoppingTheServer_LetsGoOfSessions_WithoutEndingOrDenyingThem()
    {
        var bridge = new FakeBridge().Send(Ready());
        _cli.Bridges.Enqueue(bridge);
        var host = Host();
        var id = await host.StartSessionAsync(Alice, "t", "/w/o.yaml", "/w");
        bridge.Send(new { type = "approval_request", kind = "shell_command", command = "ls" });
        await Wait.UntilAsync(() => _broker.GetPending(id).Count == 1, "the approval");

        host.Dispose();
        bridge.Exit(-1); // what a let-go relay connection looks like from here

        await Task.Delay(200);
        Assert.False(bridge.Killed);
        Assert.Empty(bridge.InputLines); // nothing was denied on the user's behalf
        Assert.Equal(SessionHostService.Kind, _cli.Tags.Single()!.Kind);
        Assert.Equal(Alice.Id, _cli.Tags.Single()!.OwnerId);
    }
}
