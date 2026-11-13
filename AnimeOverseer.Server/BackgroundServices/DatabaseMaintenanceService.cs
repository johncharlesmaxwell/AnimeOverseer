using AnimeOverseer.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AnimeOverseer.Server.BackgroundServices;

/// <summary>
/// Daily housekeeping that keeps the database small and its query plans current:
/// prunes old job logs and lets SQLite refresh its planner statistics.
/// </summary>
public class DatabaseMaintenanceService(IServiceScopeFactory scopeFactory, ILogger<DatabaseMaintenanceService> logger) : BackgroundService
{
    /// <summary>Logs of jobs that finished longer ago than this are deleted; the jobs themselves are kept.</summary>
    public static readonly TimeSpan LogRetention = TimeSpan.FromDays(90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let startup, migrations and any resumed job settle first.
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // SQLite recommends running optimize before closing long-lived connections.
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AnimeDbContext>().Database.ExecuteSqlRawAsync("PRAGMA optimize;", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "PRAGMA optimize on shutdown failed");
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var pruned = await PruneJobLogsAsync(scope.ServiceProvider.GetRequiredService<AnimeDbContext>(), DateTime.UtcNow - LogRetention, ct);
            if (pruned > 0) logger.LogInformation("Pruned {Count} job log entries older than {Days} days", pruned, LogRetention.TotalDays);
            await scope.ServiceProvider.GetRequiredService<AnimeDbContext>().Database.ExecuteSqlRawAsync("PRAGMA optimize;", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Database maintenance failed");
        }
    }

    /// <summary>Deletes the logs of jobs that finished before <paramref name="cutoff"/>.</summary>
    public static Task<int> PruneJobLogsAsync(AnimeDbContext db, DateTime cutoff, CancellationToken ct = default)
        => db.SyncJobLogs
            .Where(log => db.SyncJobs.Any(job => job.Id == log.SyncJobId && job.FinishedAt != null && job.FinishedAt < cutoff))
            .ExecuteDeleteAsync(ct);
}
