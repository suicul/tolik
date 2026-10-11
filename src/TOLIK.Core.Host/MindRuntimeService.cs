using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TOLIK.Core;

namespace TOLIK.Core.Host;

/// <summary>
/// MindRuntime host service (docs/mind.md MI-0..2 + PLAN REFORM PRG-1a): ticks the
/// ONE mind at 1 Hz — the mind lives regardless of connected renderers or users
/// (the CephalonExpressionService returns early when nobody listens; the mind never
/// freezes — TOLIK exists between conversations). §40: the loop never dies silently.
/// The service only drives the deterministic state machine — it never authorizes.
/// </summary>
public sealed class MindRuntimeService(MindRuntime mind, ILogger<MindRuntimeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Mind runtime service started: 1 Hz.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                mind.Tick(DateTimeOffset.UtcNow);
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
        logger.LogInformation("Mind runtime service stopped.");
    }
}
