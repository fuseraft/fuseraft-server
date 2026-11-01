using fuseraft.Web.Models;
using fuseraft.Web.Services.Cli;
using fuseraft.Web.Services.Users;

namespace fuseraft.Web.Services;

/// <summary>Orchestration config files, listed and validated by the fuseraft CLI.</summary>
public sealed class ConfigService(FuseraftCli cli, WorkspaceAccess access)
{
    public Task<List<WireConfigSummary>> ListConfigsAsync(string directory) =>
        cli.ListAsync<WireConfigSummary>("config", "--list", "--dir", access.Check(directory), "--json");

    public Task<WireValidation> ValidateAsync(string path) =>
        cli.ItemAsync<WireValidation>("validate", access.Check(path), "--json");
}
