using Cronos;
using fuseraft.Core;
using fuseraft.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace fuseraft.Server.Services;

/// <summary>
/// Reads and writes scheduled job YAML files in FuseraftPaths.GlobalSchedule.
/// Shares the same file format as the CLI's 'fuseraft schedule' commands.
/// </summary>
public sealed class ScheduleService
{
    private static readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public IReadOnlyList<ScheduledJob> GetJobs()
    {
        var dir = FuseraftPaths.GlobalSchedule;
        if (!Directory.Exists(dir)) return [];

        return Directory.GetFiles(dir, "*.yaml")
            .Select(f =>
            {
                try   { return _deserializer.Deserialize<ScheduledJob>(File.ReadAllText(f)); }
                catch { return null; }
            })
            .Where(j => j is not null)
            .Cast<ScheduledJob>()
            .OrderBy(j => j.NextRun ?? DateTimeOffset.MaxValue)
            .ToList();
    }

    public async Task AddJobAsync(
        string  name,
        string  cron,
        string  task,
        string? configPath  = null,
        string? description = null)
    {
        var cronExpr = CronExpression.Parse(cron);
        var slug     = Slugify(name);
        var dir      = FuseraftPaths.GlobalSchedule;
        var jobPath  = Path.Combine(dir, $"{slug}.yaml");

        if (File.Exists(jobPath))
            throw new InvalidOperationException($"Job '{slug}' already exists.");

        var job = new ScheduledJob
        {
            Name        = slug,
            Description = description,
            Cron        = cron,
            Task        = task,
            Config      = configPath,
            Enabled     = true,
            CreatedAt   = DateTimeOffset.UtcNow,
            NextRun     = cronExpr.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc),
        };

        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(jobPath, _serializer.Serialize(job));
    }

    public void RemoveJob(string name)
    {
        var slug     = Slugify(name);
        var jobPath  = Path.Combine(FuseraftPaths.GlobalSchedule, $"{slug}.yaml");
        var lockPath = Path.ChangeExtension(jobPath, ".lock");
        if (File.Exists(jobPath))  File.Delete(jobPath);
        if (File.Exists(lockPath)) File.Delete(lockPath);
    }

    public async Task ToggleJobAsync(string name, bool enabled)
    {
        var jobPath = Path.Combine(FuseraftPaths.GlobalSchedule, $"{Slugify(name)}.yaml");
        if (!File.Exists(jobPath))
            throw new FileNotFoundException($"Job '{name}' not found.");

        var job = _deserializer.Deserialize<ScheduledJob>(await File.ReadAllTextAsync(jobPath))
            ?? throw new InvalidOperationException($"Job file for '{name}' is empty or corrupt.");
        job.Enabled = enabled;
        await File.WriteAllTextAsync(jobPath, _serializer.Serialize(job));
    }

    public async Task UpdateJobRunAsync(string name)
    {
        var jobPath = Path.Combine(FuseraftPaths.GlobalSchedule, $"{Slugify(name)}.yaml");
        if (!File.Exists(jobPath)) return;

        var job = _deserializer.Deserialize<ScheduledJob>(await File.ReadAllTextAsync(jobPath))
            ?? throw new InvalidOperationException($"Job file for '{name}' is empty or corrupt.");

        job.LastRun = DateTimeOffset.UtcNow;

        CronExpression? cronExpr = null;
        try { cronExpr = CronExpression.Parse(job.Cron); } catch { }
        job.NextRun = cronExpr?.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);

        await File.WriteAllTextAsync(jobPath, _serializer.Serialize(job));
    }

    private static string Slugify(string name) =>
        System.Text.RegularExpressions.Regex
            .Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-")
            .Trim('-');
}
