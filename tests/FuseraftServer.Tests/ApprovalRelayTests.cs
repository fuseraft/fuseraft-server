using System.Text.Json;
using fuseraft.Server.Models;
using fuseraft.Server.Services;

namespace FuseraftServer.Tests;

public sealed class ApprovalRelayTests
{
    private static JsonElement Request(string kind, string command) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { type = "approval_request", kind, command })).RootElement;

    [Fact]
    public async Task Answers_AreWrittenInRequestOrder_EvenWhenGivenOutOfOrder()
    {
        var broker = new HitlBroker();
        var bridge = new FakeBridge();
        var relay  = new ApprovalRelay(broker, bridge.Input, ApprovalSource.Session);

        var first  = relay.Handle("s1", Request("shell_command", "ls"));
        var second = relay.Handle("s1", Request("shell_command", "rm -rf build"));

        broker.Respond(second.Id, false);
        await Task.Delay(50);
        Assert.Empty(bridge.InputLines); // the second answer waits for the first

        broker.Respond(first.Id, true);
        await relay.Drained;

        Assert.Equal(
            ["""{"type":"approval_response","approved":true}""", """{"type":"approval_response","approved":false}"""],
            bridge.InputLines);
    }

    [Fact]
    public async Task Interrupt_FollowsPendingAnswers()
    {
        var broker = new HitlBroker();
        var bridge = new FakeBridge();
        var relay  = new ApprovalRelay(broker, bridge.Input, ApprovalSource.Repl);
        relay.Handle("s1", Request("shell_command", "make"));

        var interrupt = relay.InterruptAsync();
        broker.CancelSession("s1");
        await interrupt;

        Assert.Equal(
            ["""{"type":"approval_response","approved":false}""", """{"type":"interrupt"}"""],
            bridge.InputLines);
    }

    [Fact]
    public void Request_CarriesItsFields()
    {
        var broker = new HitlBroker();
        var relay  = new ApprovalRelay(broker, new FakeBridge().Input, ApprovalSource.Session);
        var doc = JsonDocument.Parse("""{"type":"approval_request","kind":"file_write","plugin":"FileSystem","action":"write_file","path":"a.txt","oldContent":"","newContent":"hi"}""");

        relay.Handle("s1", doc.RootElement);

        var pending = Assert.Single(broker.GetPending("s1"));
        Assert.Equal("file_write", pending.Kind);
        Assert.Equal("a.txt", pending.Path);
        Assert.Equal("hi", pending.NewContent);
    }
}
