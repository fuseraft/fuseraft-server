using fuseraft.Cli;
using fuseraft.Core.Models;

namespace fuseraft.Server.Services;

public sealed record ConfigSummary(
    string Path,
    string Name,
    string Description,
    List<string> AgentNames,
    string SelectionType,
    string? Error = null);

public sealed record ConfigValidationResult(
    bool Valid,
    List<(string Level, string Message)> Issues,
    ConfigSummary? Summary);

public sealed class ConfigService
{
    public List<ConfigSummary> ListConfigs(string directory)
    {
        if (!Directory.Exists(directory)) return [];

        return Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".yml",  StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Order()
            .Select(f =>
            {
                try
                {
                    var cfg = OrchestratorBuilder.LoadConfig(f);
                    return new ConfigSummary(
                        f, cfg.Name, cfg.Description ?? string.Empty,
                        cfg.Agents.Select(a => a.Name).ToList(),
                        cfg.Selection.Type);
                }
                catch (Exception ex)
                {
                    return new ConfigSummary(f, Path.GetFileName(f), string.Empty, [], "?", ex.Message);
                }
            })
            .ToList();
    }

    public ConfigValidationResult Validate(string path)
    {
        var issues = new List<(string Level, string Message)>();

        if (!File.Exists(path))
        {
            issues.Add(("error", $"File not found: {path}"));
            return new(false, issues, null);
        }

        OrchestrationConfig config;
        try
        {
            config = OrchestratorBuilder.LoadConfig(path);
        }
        catch (Exception ex)
        {
            issues.Add(("error", $"Parse error: {ex.Message}"));
            return new(false, issues, null);
        }

        if (string.IsNullOrWhiteSpace(config.Name))
            issues.Add(("warning", "Orchestration.Name is empty."));

        if (!string.IsNullOrWhiteSpace(config.SystemPromptPath))
        {
            var configDir  = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".";
            var promptPath = System.IO.Path.IsPathRooted(config.SystemPromptPath)
                ? config.SystemPromptPath
                : System.IO.Path.GetFullPath(config.SystemPromptPath, configDir);
            if (!File.Exists(promptPath))
                issues.Add(("error", $"SystemPromptPath not found: {promptPath}"));
        }

        if (config.Agents.Count == 0)
        {
            issues.Add(("error", "No agents defined."));
        }
        else
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var agent in config.Agents)
            {
                if (string.IsNullOrWhiteSpace(agent.Name))
                    issues.Add(("error", "An agent has an empty Name."));
                else if (!names.Add(agent.Name))
                    issues.Add(("error", $"Duplicate agent name: '{agent.Name}'."));

                if (string.IsNullOrWhiteSpace(agent.Instructions))
                    issues.Add(("warning", $"Agent '{agent.Name}' has no Instructions."));

                if (agent.RemoteAgent is not null)
                {
                    if (string.IsNullOrWhiteSpace(agent.RemoteAgent.Url))
                        issues.Add(("error", $"Agent '{agent.Name}': RemoteAgent.Url is required."));
                    else if (!Uri.TryCreate(agent.RemoteAgent.Url, UriKind.Absolute, out _))
                        issues.Add(("error", $"Agent '{agent.Name}': RemoteAgent.Url is not a valid URL."));
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(agent.Model?.ModelId))
                        issues.Add(("error", $"Agent '{agent.Name}': ModelId is empty."));

                    if (!string.IsNullOrWhiteSpace(agent.Model?.ApiKeyEnvVar)
                        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(agent.Model.ApiKeyEnvVar)))
                        issues.Add(("warning", $"Agent '{agent.Name}': env var '{agent.Model.ApiKeyEnvVar}' is not set."));
                }

                if (agent.FunctionChoice.ToLowerInvariant() is not ("auto" or "required" or "none"))
                    issues.Add(("error", $"Agent '{agent.Name}': FunctionChoice '{agent.FunctionChoice}' is invalid (auto/required/none)."));
            }
        }

        var selType = config.Selection.Type.ToLowerInvariant();
        if (selType is not ("sequential" or "roundrobin" or "llm" or "keyword" or "structured" or "magentic" or "statemachine" or "graph"))
            issues.Add(("error", $"Unknown selection type: '{config.Selection.Type}'."));

        if (selType == "llm" && config.Selection.Model is null)
            issues.Add(("error", "LLM selection requires Selection.Model."));

        if (selType == "keyword" && (config.Selection.Routes is null || config.Selection.Routes.Count == 0))
            issues.Add(("error", "Keyword selection requires at least one Route."));

        if (selType == "graph" && config.Selection.Graph is null)
            issues.Add(("error", "Graph selection requires a Selection.Graph block."));

        if (selType == "statemachine" && config.Selection.StateMachine is null)
            issues.Add(("error", "StateMachine selection requires a Selection.StateMachine block."));

        if (selType == "magentic" && config.Selection.Magentic?.Model is null)
            issues.Add(("error", "Magentic selection requires Selection.Magentic.Model."));

        if (config.Termination is not null)
        {
            var tt = config.Termination.Type.ToLowerInvariant();
            if (tt is not ("regex" or "maxiterations" or "composite"))
                issues.Add(("error", $"Unknown termination type: '{config.Termination.Type}'."));
            if (tt == "regex" && string.IsNullOrWhiteSpace(config.Termination.Pattern))
                issues.Add(("error", "Regex termination requires a Pattern."));
        }

        var summary = new ConfigSummary(
            path, config.Name, config.Description ?? string.Empty,
            config.Agents.Select(a => a.Name).ToList(),
            config.Selection.Type);

        return new(issues.All(i => i.Level != "error"), issues, summary);
    }
}
