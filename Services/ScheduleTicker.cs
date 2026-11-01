using Microsoft.Extensions.Options;
using fuseraft.Web.Services.Cli;
using fuseraft.Web.Services.Users;

namespace fuseraft.Web.Services;

/// <summary>
/// Runs <c>fuseraft schedule run</c> in every account's home once a minute, with that account's profile keys,
/// so each user's scheduled jobs run without a cron entry per home. fuseraft's own job locks keep a tick from
/// this and one from the host's cron from running a job twice.
/// </summary>
public sealed class ScheduleTicker(
    FuseraftCli cli, UserStore users, ModelProfileRegistry profiles, IOptions<WebOptions> options, ILogger<ScheduleTicker> logger)
    : BackgroundService
{
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    // A tick runs due jobs to completion, so one user's long job must hold up neither the next tick nor anyone else.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunSchedules) return;
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                _ = TickAsync();
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    internal Task TickAsync()
    {
        var started = new List<Task>();
        foreach (var user in users.All.Where(u => !u.Disabled && Directory.Exists(Path.Combine(u.Home, "schedule"))))
        {
            if (_running.TryGetValue(user.Id, out var previous) && !previous.IsCompleted) continue;
            var run = RunAsync(user);
            _running[user.Id] = run;
            started.Add(run);
        }
        return Task.WhenAll(started);
    }

    private async Task RunAsync(WebUser user)
    {
        try
        {
            var keys = await profiles.For(user).KeyEnvironmentAsync();
            await cli.ForHome(user.Home).RunAsync(keys, "schedule", "run");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scheduled jobs of {User} didn't run", user.UserName);
        }
    }
}
