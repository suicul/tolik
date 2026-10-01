using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TOLIK.Core;
using TOLIK.Infrastructure.Persistence;

namespace TOLIK.Core.Host;

/// <summary>
/// Task watchdog (§22 autonomy stays bounded, §23 proactive): once a minute,
/// converts task-table outcomes into durable proactive notifications —
/// failed tasks (High), finished tasks (Low), tasks stuck Running (Normal).
/// Scheduling is idempotent per task (reason-keyed); delivery reuses the
/// existing reminder pipeline, so nothing new can spam the client.
/// </summary>
public sealed class TaskWatchdogBackgroundService(
    IDbContextFactory<TolikDbContext> dbFactory,
    ProactiveStore proactive,
    ILogger<TaskWatchdogBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Task watchdog pass failed; retrying with backoff.");
                try { await Task.Delay(ErrorBackoff, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PassAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.Tasks.AsNoTracking()
            .Where(x => x.Status == "Failed" || x.Status == "Completed" || x.Status == "Running")
            .Select(x => new { x.Id, x.UserId, x.Description, x.Status, x.CreatedAt, x.CompletedAt })
            .ToListAsync(cancellationToken);

        var scheduled = 0;
        foreach (var task in candidates)
        {
            WatchdogNotification? notice = task.Status switch
            {
                "Failed" when task.CompletedAt >= now - TaskWatchdog.OutcomeWindow
                    => TaskWatchdog.ForFailed(task.UserId, task.Id, task.Description),
                "Completed" when task.CompletedAt >= now - TaskWatchdog.OutcomeWindow
                    => TaskWatchdog.ForCompleted(task.UserId, task.Id, task.Description),
                "Running" => TaskWatchdog.ForStaleRunning(task.UserId, task.Id, task.Description, task.CreatedAt, now),
                _ => null,
            };
            if (notice is null) continue;
            if (await proactive.ExistsAsync(notice.UserId, notice.Reason, cancellationToken)) continue;
            await proactive.ScheduleAsync(notice.UserId, notice.Text, notice.Priority, now, notice.Reason, cancellationToken);
            scheduled++;
        }

        if (scheduled > 0)
            logger.LogInformation("Task watchdog scheduled {Count} notifications.", scheduled);
    }
}
