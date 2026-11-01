using fuseraft.Web.Services;

namespace FuseraftWeb.Tests;

public sealed class MemoryServiceTests
{
    [Fact]
    public async Task GetAgentMemories_ListsEachAgentReported()
    {
        var cli = new FakeCli();
        cli.Responses["memory agents --json"] = """{"protocolVersion":1,"items":[{"agent":"reviewer","count":1}]}""";
        cli.Responses["memory list --agent reviewer --json"] =
            """{"protocolVersion":1,"items":[{"name":"style","type":"feedback","description":"d","body":"b"}]}""";

        var result = await new MemoryService(cli).GetAgentMemoriesAsync();

        Assert.Equal("style", Assert.Single(Assert.Single(result).Value).Name);
    }

    [Fact]
    public async Task DeleteAgentMemory_TargetsTheAgentStore()
    {
        var cli = new FakeCli();
        await new MemoryService(cli).DeleteAgentMemoryAsync("reviewer", "style");
        Assert.Equal(["memory", "delete", "style", "--agent", "reviewer"], Assert.Single(cli.Runs));
    }
}
