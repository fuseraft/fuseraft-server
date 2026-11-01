using fuseraft.Web.Services;
using fuseraft.Web.Services.Users;

namespace FuseraftWeb.Tests;

public sealed class ScheduleServiceTests
{
    [Fact]
    public async Task AddJob_PassesOnlyTheOptionsGiven()
    {
        var cli = new FakeCli();
        await new ScheduleService(cli, WorkspaceAccess.Unrestricted).AddJobAsync("nightly", "0 2 * * *", "Audit", configPath: "", description: "d", workDir: "/w");
        Assert.Equal(
            ["schedule", "add", "nightly", "--cron", "0 2 * * *", "--task", "Audit", "--description", "d", "--work-dir", Path.GetFullPath("/w")],
            Assert.Single(cli.Runs));
    }

    [Theory]
    [InlineData(true, "enable")]
    [InlineData(false, "disable")]
    public async Task SetEnabled_UsesEnableOrDisable(bool enabled, string verb)
    {
        var cli = new FakeCli();
        await new ScheduleService(cli, WorkspaceAccess.Unrestricted).SetEnabledAsync("nightly", enabled);
        Assert.Equal(["schedule", verb, "nightly"], Assert.Single(cli.Runs));
    }

    [Fact]
    public async Task GetJobs_ReadsTimestamps()
    {
        var cli = new FakeCli();
        cli.Responses["schedule list --json"] =
            """{"protocolVersion":1,"items":[{"name":"nightly","cron":"0 2 * * *","task":"t","enabled":true,"due":true,"createdAt":"2026-10-31T00:00:00+00:00","lastRun":null,"nextRun":"2026-11-01T02:00:00+00:00"}]}""";

        var job = Assert.Single(await new ScheduleService(cli, WorkspaceAccess.Unrestricted).GetJobsAsync());

        Assert.True(job.Due);
        Assert.Null(job.LastRun);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 2, 0, 0, TimeSpan.Zero), job.NextRun);
    }
}
