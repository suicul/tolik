using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TOLIK.Core;
using TOLIK.Infrastructure.Persistence;

namespace TOLIK.Core.Host;

/// <summary>
/// Schedules the daily episodic review (§19). Fires once per day at the configured
/// local hour (default 23:30); on startup it catches up if yesterday was missed.
/// Failures never crash the host.
/// </summary>
public sealed class DailyReviewBackgroundService(
    IDbContextFactory<TolikDbContext> dbFactory,
    DailyReviewService review,
    ILogger<DailyReviewBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);
    private DateTimeOffset? _lastAttemptDay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.Now;
                var reviewHour = 23;
                var reviewMinute = 30;
                var todaySlot = new DateTime(now.Year, now.Month, now.Day, reviewHour, reviewMinute, 0);

                // Run once per calendar day, after the slot time (or catch up on missed days).
                var todayKey = now.Date;
                var due = now.DateTime >= todaySlot && _lastAttemptDay != todayKey;
                if (due)
                {
                    _lastAttemptDay = todayKey;
                    var dayStart = new DateTimeOffset(now.Date, now.Offset);
                    await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                    var owners = await db.Users.Where(x => !x.Revoked).Select(x => x.Id).ToListAsync(stoppingToken);
                    foreach (var owner in owners)
                        await review.RunForOwnerAsync(owner, dayStart, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Daily review tick failed.");
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
