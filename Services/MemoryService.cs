using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;

namespace fuseraft.Server.Services;

public sealed class MemoryService
{
    private static string AgentsRoot =>
        Path.Combine(FuseraftPaths.GlobalRoot, "memory", "agents");

    public Task<List<MemoryEntry>> GetReplMemoriesAsync() =>
        MemoryStore.ForRepl().LoadAllAsync();

    public async Task<Dictionary<string, List<MemoryEntry>>> GetAgentMemoriesAsync()
    {
        if (!Directory.Exists(AgentsRoot)) return [];

        var result = new Dictionary<string, List<MemoryEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.GetDirectories(AgentsRoot).OrderBy(d => d))
        {
            var agentName = Path.GetFileName(dir);
            var entries   = await MemoryStore.ForAgent(agentName).LoadAllAsync();
            if (entries.Count > 0)
                result[agentName] = entries;
        }
        return result;
    }

    public Task<bool> DeleteReplMemoryAsync(string name) =>
        MemoryStore.ForRepl().DeleteAsync(name);

    public Task<bool> DeleteAgentMemoryAsync(string agentName, string name) =>
        MemoryStore.ForAgent(agentName).DeleteAsync(name);
}
