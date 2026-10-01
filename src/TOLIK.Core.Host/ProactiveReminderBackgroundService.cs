using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Threading.Tasks;
using TOLIK.Core.Host;
using TOLIK.Infrastructure.Persistence;

namespace TOLIK.Core.Host;

/// <summary>
/// Background service that polls for due proactive reminders and pushes them
/// via WebSocket to connected clients (§23, §29).
/// </summary>
public sealed class ProactiveReminderBackgroundService(
    IDbContextFactory<TolikDbContext> dbFactory,
    RealtimeEventHub realtime,
    ProactiveStore proactive,
    ILogger<ProactiveReminderBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay first run so startup stays fast and migrations finish first.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delivered = 0;
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                var usersWithDue = await db.Proactive
                    .Where(x => x.DeliveredAt == null && x.DueAt <= DateTimeOffset.UtcNow)
                    .Select(x => x.UserId)
                    .Distinct()
                    .ToListAsync(stoppingToken);

                foreach (var userId in usersWithDue)
                {
                    var due = await proactive.TakeDueAsync(userId, 16, stoppingToken);
                    foreach (var message in due)
                    {
                        realtime.PublishToUser(message.UserId, JsonSerializer.Serialize(new
                        {
                            type = "proactive_reminder",
                            id = message.Id,
                            text = message.Text,
                            priority = message.Priority,
                            dueAt = message.DueAt,
                            reason = message.Reason
                        }));
                        delivered++;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Proactive reminder pass failed; retrying with backoff.");
                try { await Task.Delay(ErrorBackoff, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }

            if (delivered > 0)
                logger.LogInformation("Proactive reminder pass finished: {Count} reminders delivered.", delivered);

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}