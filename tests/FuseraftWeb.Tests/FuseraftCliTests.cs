using System.Text.Json;
using fuseraft.Web.Models;
using fuseraft.Web.Services.Cli;

namespace FuseraftWeb.Tests;

public sealed class FuseraftCliTests
{
    private static JsonDocument Doc(string json) => JsonDocument.Parse(json);

    [Fact]
    public void ParseList_ReadsItems()
    {
        var items = FuseraftCli.ParseList<WireSession>(Doc(
            """{"protocolVersion":1,"items":[{"sessionId":"abcd1234","task":"t","isComplete":true,"turnCount":3,"futureField":1}]}"""));

        var s = Assert.Single(items);
        Assert.Equal("abcd1234", s.SessionId);
        Assert.True(s.IsComplete);
        Assert.Equal(3, s.TurnCount);
    }

    [Fact]
    public void ParseItem_ReadsItem()
    {
        var detail = FuseraftCli.ParseItem<WireSessionDetail>(Doc(
            """{"protocolVersion":1,"item":{"sessionId":"abcd1234","messages":[{"agentName":"Dev","content":"hi","toolCalls":[{"name":"write_file","succeeded":true}]}]}}"""));

        var msg = Assert.Single(detail.Messages);
        Assert.Equal("Dev", msg.AgentName);
        Assert.Equal("write_file", Assert.Single(msg.ToolCalls).Name);
    }

    [Fact]
    public void ErrorEnvelope_Throws()
    {
        var ex = Assert.Throws<FuseraftCliException>(() =>
            FuseraftCli.ParseList<WireSession>(Doc("""{"protocolVersion":1,"error":"Session not found: x"}""")));
        Assert.Contains("Session not found", ex.Message);
    }

    [Fact]
    public void OtherProtocolVersion_Throws()
    {
        var ex = Assert.Throws<FuseraftCliException>(() =>
            FuseraftCli.ParseList<WireSession>(Doc("""{"protocolVersion":2,"items":[]}""")));
        Assert.Contains("protocol version 2", ex.Message);
    }

    [Fact]
    public void MissingProtocolVersion_ExplainsTheUpgrade()
    {
        var ex = Assert.Throws<FuseraftCliException>(() =>
            FuseraftCli.ParseList<WireSession>(Doc("""{"items":[]}""")));
        Assert.Contains("fuseraft update", ex.Message);
    }

    [Fact]
    public void NonEnvelope_Throws()
    {
        Assert.Throws<FuseraftCliException>(() => FuseraftCli.ParseList<WireSession>(Doc("[]")));
    }
}
