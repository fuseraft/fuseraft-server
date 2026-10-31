using fuseraft.Server.Models;
using fuseraft.Server.Services.Cli;

namespace fuseraft.Server.Services;

public sealed class MemoryService(FuseraftCli cli)
{
    public Task<List<WireMemory>> GetReplMemoriesAsync() =>
        cli.ListAsync<WireMemory>("memory", "list", "--json");

    public async Task<Dictionary<string, List<WireMemory>>> GetAgentMemoriesAsync()
    {
        var result = new Dictionary<string, List<WireMemory>>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in await cli.ListAsync<WireMemoryAgent>("memory", "agents", "--json"))
            result[a.Agent] = await cli.ListAsync<WireMemory>("memory", "list", "--agent", a.Agent, "--json");
        return result;
    }

    public Task DeleteReplMemoryAsync(string name) =>
        cli.RunAsync("memory", "delete", name);

    public Task DeleteAgentMemoryAsync(string agentName, string name) =>
        cli.RunAsync("memory", "delete", name, "--agent", agentName);
}
