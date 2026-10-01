using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TOLIK.Core;
using TOLIK.Infrastructure.Persistence;

namespace TOLIK.Core.Host;

/// <summary>
/// Runs memory compaction periodically for every active user.
/// Failures never crash the host: each pass is isolated and logged.
/// </summary>
public sealed class MemoryCompactionBackgroundService(
    IDbContextFactory<TolikDbContext> dbFactory,
    MemoryCompactionService compaction,
    ILogger<MemoryCompactionBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay first run so startup stays fast and migrations finish first.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            var compacted = 0;
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                var ownerIds = await db.Users
                    .Where(x => !x.Revoked)
                    .Select(x => x.Id)
                    .ToListAsync(stoppingToken);

                foreach (var ownerId in ownerIds)
                {
                    compacted += await compaction.CompactOwnerAsync(ownerId, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Memory compaction pass failed; retrying with backoff.");
                try { await Task.Delay(ErrorBackoff, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }

            if (compacted > 0)
                logger.LogInformation("Memory compaction pass finished: {Count} entries compacted.", compacted);

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
