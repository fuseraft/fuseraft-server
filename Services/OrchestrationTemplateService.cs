using fuseraft.Server.Models;
using fuseraft.Server.Services.Cli;

namespace fuseraft.Server.Services;

/// <summary>The team templates <c>fuseraft init</c> generates configs from.</summary>
public sealed class OrchestrationTemplateService(FuseraftCli cli)
{
    public Task<List<WireTemplate>> ListAsync() =>
        cli.ListAsync<WireTemplate>("init", "--list-templates", "--json");

    /// <summary>Writes a config and its agent files; init also sets up <c>.fuseraft/</c> in the workspace.</summary>
    public Task<WireInitResult> GenerateAsync(string workspace, string template, string outputPath,
        string? model, string? endpoint, bool force)
    {
        List<string> args = ["init", outputPath, "--template", template, "--json"];
        if (!string.IsNullOrWhiteSpace(model))    args.AddRange(["--model", model]);
        if (!string.IsNullOrWhiteSpace(endpoint)) args.AddRange(["--endpoint", endpoint]);
        if (force) args.Add("--force");
        return cli.ItemInAsync<WireInitResult>(workspace, [.. args]);
    }
}
