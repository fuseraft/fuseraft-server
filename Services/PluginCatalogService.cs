using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Server.Services;

public sealed record PluginFunctionEntry(string Name, string Description);

public sealed record PluginEntry(
    string Name,
    string Description,
    IReadOnlyList<PluginFunctionEntry> Functions);

public sealed class PluginCatalogService
{
    public IReadOnlyList<PluginEntry> Plugins { get; }

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

    public PluginCatalogService(PluginRegistry registry)
    {
        var entries = new List<PluginEntry>();

        foreach (var name in registry.RegisteredPlugins.OrderBy(n => n))
        {
            IReadOnlyList<PluginFunctionEntry> fns;

            if (registry.TryGetAIFunctions(name, out var aiFns))
            {
                fns = aiFns
                    .OrderBy(f => f.Name)
                    .Select(f => new PluginFunctionEntry(f.Name, f.Description ?? string.Empty))
                    .ToList();
            }
            else if (registry.TryGet(name, out var plugin))
            {
                fns = PluginRegistry.GetFunctionsFromObject(plugin)
                    .Select(f => new PluginFunctionEntry(f.Name, f.Description ?? string.Empty))
                    .ToList();
            }
            else
            {
                fns = [];
            }

            var desc = _descriptions.TryGetValue(name, out var d) ? d : string.Empty;
            entries.Add(new PluginEntry(name, desc, fns));
        }

        Plugins = entries;
    }

    public PluginEntry? Get(string name) =>
        Plugins.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
