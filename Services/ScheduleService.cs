using fuseraft.Web.Models;
using fuseraft.Web.Services.Cli;
using fuseraft.Web.Services.Users;

namespace fuseraft.Web.Services;

/// <summary>Scheduled jobs, managed through <c>fuseraft schedule</c>.</summary>
public sealed class ScheduleService(FuseraftCli cli, WorkspaceAccess access)
{
    public Task<List<WireScheduledJob>> GetJobsAsync() =>
        cli.ListAsync<WireScheduledJob>("schedule", "list", "--json");

    public Task AddJobAsync(string name, string cron, string task,
        string? configPath = null, string? description = null, string? workDir = null)
    {
        List<string> args = ["schedule", "add", name, "--cron", cron, "--task", task];
        if (!string.IsNullOrWhiteSpace(configPath))  args.AddRange(["--config", access.Check(configPath, workDir)]);
        if (!string.IsNullOrWhiteSpace(description)) args.AddRange(["--description", description]);
        if (!string.IsNullOrWhiteSpace(workDir))     args.AddRange(["--work-dir", access.Check(workDir)]);
        else if (access.Roots.Count > 0)              throw new ArgumentException("Pick a working directory for the job.");
        return cli.RunAsync([.. args]);
    }

    public Task RemoveJobAsync(string name) =>
        cli.RunAsync("schedule", "remove", name);

    public Task SetEnabledAsync(string name, bool enabled) =>
        cli.RunAsync("schedule", enabled ? "enable" : "disable", name);
}
