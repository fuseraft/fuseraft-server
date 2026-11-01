using fuseraft.Web.Services.Cli;
using fuseraft.Web.Services.Users;

namespace fuseraft.Web.Services.Relay;

/// <summary>
/// At startup, takes over the sessions and chats a previous server left running in relays. Each relay is
/// connected once and handed to its owner service: a second connection would replace the first.
/// </summary>
public sealed class RelayAdopter(
    FuseraftCli cli, SessionHostService sessions, ReplService repl, UserStore users, ILogger<RelayAdopter> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var running = await cli.ReattachBridgesAsync();
        foreach (var (tag, process) in running)
        {
            switch (tag.Kind)
            {
                case SessionHostService.Kind:
                    _ = sessions.AdoptRunningAsync(tag, process);
                    break;
                case ReplService.Kind:
                    repl.AdoptRunning(tag, process, users.Find(tag.OwnerId));
                    break;
                default:
                    process.Kill();
                    process.Dispose();
                    break;
            }
        }
        if (running.Count > 0)
            logger.LogInformation("Took over {Count} running session(s) and chat(s) from the previous server.", running.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
