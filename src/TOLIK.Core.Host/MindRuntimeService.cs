using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TOLIK.Core;
using TOLIK.Infrastructure.Persistence;

namespace TOLIK.Core.Host;

/// <summary>
/// MindRuntime host service (docs/mind.md MI-0..2 + PLAN REFORM PRG-1a): ticks the
/// ONE mind at 1 Hz — the mind lives regardless of connected renderers or users
/// (the CephalonExpressionService returns early when nobody listens; the mind never
/// freezes — TOLIK exists between conversations). §40: the loop never dies silently.
/// The service only drives the deterministic state machine — it never authorizes.
///
/// PLAN-ЭТАЛОН Phase 2.3 (blocker Б3): it also PERSISTS the mind. Before this, MindState
/// was a private field of a singleton, so a restart reset boredom/curiosity/attention and
/// the consecutive-failure streak. The save is periodic (one upserted row, trivial volume)
/// and once more on shutdown; a failed save is reported but never kills the mind.
/// </summary>
public sealed class MindRuntimeService(
    MindRuntime mind,
    MindSnapshotStore snapshots,
    ILogger<MindRuntimeService> logger) : BackgroundService
{
    /// <summary>
    /// How often the mind is written to the database. The mind ticks at 1 Hz but its state
    /// moves on interactions and task outcomes, not on the tick — 15 s bounds the loss to a
    /// handful of seconds while keeping the write volume trivial.
    /// </summary>
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Mind runtime service started: 1 Hz, persisting every {Interval}s.", PersistInterval.TotalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var sinceLastSave = TimeSpan.Zero;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await timer.WaitForNextTickAsync(stoppingToken);
                    mind.Tick(DateTimeOffset.UtcNow);

                    sinceLastSave += TimeSpan.FromSeconds(1);
                    if (sinceLastSave >= PersistInterval)
                    {
                        sinceLastSave = TimeSpan.Zero;
                        await PersistAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Mind tick failed; continuing.");
                }
            }
        }
        finally
        {
            // Shutdown save: the last few seconds of the mind are worth keeping, and a clean
            // stop should not depend on having landed exactly on a periodic boundary.
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await PersistAsync(shutdown.Token);
            logger.LogInformation("Mind runtime service stopped.");
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        try
        {
            await snapshots.SaveAsync(mind.CaptureContinuity(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown budget exhausted — nothing to report beyond the stop itself.
        }
        catch (Exception ex)
        {
            // Persistence is continuity, not correctness: a failed write must not stop the
            // mind from living. It is logged so the gap stays visible rather than silent.
            logger.LogWarning(ex, "Mind snapshot could not be persisted; will retry on the next interval.");
        }
    }
}
