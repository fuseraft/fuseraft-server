using fuseraft.Web.Models;
using fuseraft.Web.Services.Cli;

namespace fuseraft.Web.Services;

public sealed record PluginFunctionEntry(string Name, string Description);

public sealed record PluginEntry(
    string Name,
    string Description,
    IReadOnlyList<PluginFunctionEntry> Functions);

public sealed class PluginCatalogService(FuseraftCli cli)
{
    private readonly Lock _gate = new();
    private Task<IReadOnlyList<PluginEntry>>? _plugins;

    // Plugin-level descriptions derived from the registry XML doc.
    private static readonly Dictionary<string, string> _descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Changes"]       = "Read-only view of the session change log written by the orchestrator.",
        ["Chatroom"]      = "Shared append-only message log for agent-to-agent coordination.",
        ["CodeExecution"] = "Docker-backed sandboxed execution and persistent REPL sessions for Python and Node.js.",
        ["Document"]      = "Extract text and metadata from PDF, DOCX, PPTX, and XLSX files.",
        ["FileSystem"]    = "Read, write, list, copy, move, patch, and delete local files and directories.",
        ["Git"]           = "Common Git operations: status, diff, log, commit, branch, push, pull, stash.",
        ["Handoff"]       = "Type-safe routing signal; terminates the tool loop and hands off to the next workflow step.",
        ["Http"]          = "HTTP GET, POST, PUT, PATCH, DELETE, and HEAD requests to external URLs.",
        ["Json"]          = "Format, minify, query, merge, and validate JSON data.",
        ["Probe"]         = "Run code snippets, assert outputs with PASS/FAIL verdicts, and test hypotheses.",
        ["Scratchpad"]    = "Per-agent persistent key-value store that survives across sessions.",
        ["Search"]        = "Find files by name pattern, grep content, and locate symbol/call-site definitions.",
        ["Shell"]         = "Execute shell commands and scripts; manage background jobs and environment variables.",
        ["SubAgent"]      = "Spawn sub-agent loops for broad codebase exploration and symbol lookup.",
    };

    /// <summary>Every plugin fuseraft registers; fetched once (and again after a failure), since the set only changes with a fuseraft upgrade.</summary>
    public Task<IReadOnlyList<PluginEntry>> GetPluginsAsync()
    {
        lock (_gate)
        {
            if (_plugins is null || _plugins.IsFaulted) _plugins = LoadAsync(cli);
            return _plugins;
        }
    }

    private static async Task<IReadOnlyList<PluginEntry>> LoadAsync(FuseraftCli cli)
    {
        var plugins = await cli.ListAsync<WirePlugin>("plugins", "--json");
        return [.. plugins.Select(p => new PluginEntry(
            p.Name,
            _descriptions.TryGetValue(p.Name, out var d) ? d : string.Empty,
            [.. p.Functions.Select(f => new PluginFunctionEntry(f.Name, f.Description))]))];
    }
}
