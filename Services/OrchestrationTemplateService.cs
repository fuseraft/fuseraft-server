using fuseraft.Cli.Commands;

namespace fuseraft.Server.Services;

public sealed record TemplateInfo(
    string Key,
    string Name,
    string Description,
    string Icon,
    string[] DefaultAgents,
    string SelectionType);

public sealed class OrchestrationTemplateService
{
    public static readonly IReadOnlyList<TemplateInfo> Templates =
    [
        new("dev-team",        "Dev Team",        "Planner → Developer → Tester → Reviewer pipeline with evidence contracts and state-machine routing.", "🧑‍💻", ["Planner", "Developer", "Tester", "Reviewer"], "statemachine"),
        new("minimal",         "Minimal",         "Single general-purpose agent for simple, self-contained tasks.", "⚡", ["Agent"], "sequential"),
        new("research",        "Research",        "Researcher + Analyst + Writer pipeline for deep investigation and reporting.", "🔬", ["Researcher", "Analyst", "Writer"], "sequential"),
        new("devops",          "DevOps",          "Planner → Coder → Tester → Deployer pipeline for infrastructure and CI/CD work.", "⚙️", ["Planner", "Coder", "Tester", "Deployer"], "sequential"),
        new("content",         "Content",         "Researcher → Writer → Editor → Publisher pipeline for content creation.", "✍️", ["Researcher", "Writer", "Editor", "Publisher"], "sequential"),
        new("magentic",        "Magentic",        "LLM-driven dynamic orchestration that selects agents based on conversation context.", "🧲", ["Orchestrator", "Specialist"], "magentic"),
        new("designer",        "Designer",        "Single design-focused agent for UX, UI and product-design tasks.", "🎨", ["Designer"], "sequential"),
        new("brownfield",      "Brownfield",      "Planner → Developer → Tester pipeline tuned for existing codebases.", "🏗️", ["Planner", "Developer", "Tester"], "statemachine"),
        new("graph",           "Graph",           "Directed-graph orchestration letting agents hand off to arbitrary peers.", "🕸️", ["Coordinator", "Specialist-A", "Specialist-B"], "graph"),
        new("brownfield-graph","Brownfield Graph","Graph-based pipeline optimised for large, legacy codebases.", "🗺️", ["Planner", "Explorer", "Developer", "Reviewer"], "graph"),
    ];

    public GeneratedConfig Build(string templateKey, string model, string? endpoint = null) =>
        InitTemplates.Build(templateKey, model, endpoint);

    public TemplateInfo? GetTemplate(string key) =>
        Templates.FirstOrDefault(t => t.Key == key);
}
