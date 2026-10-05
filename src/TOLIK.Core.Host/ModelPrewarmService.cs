using System.Diagnostics;
using TOLIK.Infrastructure.Models;

namespace TOLIK.Core.Host;

/// <summary>
/// Model prewarm (voice-runtime-v2.md M7): the local LLM cold load dominates the first
/// turn (measured 9.6s TTFT cold — the model load after Ollama start). Pinging the
/// model route once at startup moves that load out of the user's first turn; keep_alive
/// (60m, already in the request builder) keeps it warm between turns. Cloud routes need
/// no prewarm (always warm). Never fails the app: a prewarm failure only logs.
/// </summary>
public sealed class ModelPrewarmService(ModelRouter router, ModelCompletionClient completion, ILogger<ModelPrewarmService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let the API and dependencies come up before the prewarm ping.
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            var route = router.ResolveWithFallback(ModelTaskKind.Simple);
            if (route is null)
            {
                logger.LogInformation("Model prewarm skipped: no Simple route configured.");
                return;
            }
            if (string.Equals(route.Provider, "ollama", StringComparison.OrdinalIgnoreCase))
            {
                var sw = Stopwatch.StartNew();
                // Minimal generation: the point is the model load, not the answer.
                // Fixed literal prompt — no private data (SECURITY §38).
                await completion.CompleteAsync(route, new ModelRequest(route.Model, "ping", NumPredict: 1, Think: false, NumCtx: 512), TimeSpan.FromSeconds(120), stoppingToken);
                logger.LogInformation("Model prewarm done in {ElapsedMs}ms (model {Model}, {Provider})", sw.ElapsedMilliseconds, route.Model, route.Provider);
            }
            else
            {
                logger.LogInformation("Model prewarm skipped: provider {Provider} needs no prewarm.", route.Provider);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown — nothing to do.
        }
        catch (Exception ex)
        {
            logger.LogWarning("Model prewarm skipped: {Message}", ex.Message);
        }
    }
}
