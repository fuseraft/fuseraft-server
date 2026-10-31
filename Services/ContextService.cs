using fuseraft.Server.Models;
using fuseraft.Server.Services.Cli;

namespace fuseraft.Server.Services;

public sealed class ContextService(FuseraftCli cli)
{
    public Task<List<WireContextItem>> ListAsync(string workspaceDir) =>
        cli.ListAsync<WireContextItem>("context", "list", "--dir", workspaceDir, "--json");

    public Task AddAsync(string workspaceDir, string sourcePath, string name, string? description = null)
    {
        List<string> args = ["context", "add", sourcePath, "--name", name, "--dir", workspaceDir];
        if (!string.IsNullOrWhiteSpace(description)) args.AddRange(["--description", description]);
        return cli.RunAsync([.. args]);
    }

    public Task RemoveAsync(string workspaceDir, string name) =>
        cli.RunAsync("context", "remove", name, "--dir", workspaceDir);
}
