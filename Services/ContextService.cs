using fuseraft.Web.Models;
using fuseraft.Web.Services.Cli;
using fuseraft.Web.Services.Users;

namespace fuseraft.Web.Services;

public sealed class ContextService(FuseraftCli cli, WorkspaceAccess access)
{
    public Task<List<WireContextItem>> ListAsync(string workspaceDir) =>
        cli.ListAsync<WireContextItem>("context", "list", "--dir", access.Check(workspaceDir), "--json");

    public Task AddAsync(string workspaceDir, string sourcePath, string name, string? description = null)
    {
        List<string> args = ["context", "add", access.Check(sourcePath), "--name", name, "--dir", access.Check(workspaceDir)];
        if (!string.IsNullOrWhiteSpace(description)) args.AddRange(["--description", description]);
        return cli.RunAsync([.. args]);
    }

    public Task RemoveAsync(string workspaceDir, string name) =>
        cli.RunAsync("context", "remove", name, "--dir", access.Check(workspaceDir));
}
