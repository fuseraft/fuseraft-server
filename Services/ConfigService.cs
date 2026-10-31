using fuseraft.Server.Models;
using fuseraft.Server.Services.Cli;

namespace fuseraft.Server.Services;

/// <summary>Orchestration config files, listed and validated by the fuseraft CLI.</summary>
public sealed class ConfigService(FuseraftCli cli)
{
    public Task<List<WireConfigSummary>> ListConfigsAsync(string directory) =>
        cli.ListAsync<WireConfigSummary>("config", "--list", "--dir", directory, "--json");

    public Task<WireValidation> ValidateAsync(string path) =>
        cli.ItemAsync<WireValidation>("validate", path, "--json");
}
