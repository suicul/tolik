using Microsoft.Extensions.Logging;
using TOLIK.Core;
using TOLIK.Infrastructure.Models;

namespace TOLIK.Infrastructure.Persistence;

/// <summary>
/// Builds the daily episodic memory (AGENTS.md §19): once per day, gathers
/// the day's conversation/voice/reminder memories and produces a short "Итог дня"
/// summary stored with moderate confidence. Model output is data, never fact (§49).
/// Idempotent per (owner, date) via the memory source key.
/// </summary>
public sealed class DailyReviewService(
    DurableMemoryStore durableMemory,
    ModelRouter router,
    ModelCompletionClient completion,
    ILogger<DailyReviewService> logger)
{
    public const string SourcePrefix = "daily.review";

    /// <summary>Minimum day activity (memory items) required to produce a review.</summary>
    public const int MinimumActivity = 5;

    public async Task<bool> RunForOwnerAsync(Guid ownerId, DateTimeOffset dayStart, CancellationToken cancellationToken = default)
    {
        var sourceKey = $"{SourcePrefix}:{dayStart:yyyyMMdd}";
        if (await durableMemory.ExistsSinceAsync(ownerId, sourceKey, dayStart, cancellationToken))
            return false; // already reviewed

        var day = await durableMemory.GetDayAsync(ownerId, dayStart, cancellationToken);
        if (day.Count < MinimumActivity)
        {
            logger.LogInformation("Daily review skipped for {Owner}: only {Count} items.", ownerId, day.Count);
            return false;
        }

        var route = router.Resolve(ModelTaskKind.Simple);
        if (route is null)
        {
            logger.LogWarning("Daily review skipped: no model route configured.");
            return false;
        }

        var corpus = string.Join("\n", day
            .Where(m => m.Content.Length <= 512)
            .Select(m => $"[{m.CreatedAt:HH:mm}] {m.Content}"));

        var prompt =
            "Ты — система формирования эпизодической памяти личного ассистента TOLIK. " +
            "Составь краткий итог дня (3-5 предложений) на русском языке по перечисленным событиям. " +
            "Не выдумывай. Сохраняй имена, времена и договорённости. " +
            "Ответ — только текст итога.\n\n" + corpus;

        string summary;
        try
        {
            summary = (await completion.CompleteAsync(route, new ModelRequest(route.Model, prompt), TimeSpan.FromSeconds(90), cancellationToken)).Trim();
        }
        catch (ModelProviderUnavailableException ex)
        {
            logger.LogWarning("Daily review skipped: model unavailable ({Message}).", ex.Message);
            return false;
        }

        if (summary.Length == 0 || summary.Length > 4096) return false;

        await durableMemory.AddAsync(
            ownerId,
            MemoryScope.Private,
            $"[Итог дня {dayStart:dd.MM.yyyy}] {summary}",
            sourceKey,
            0.7, // reviewed-but-inferred: never full confidence (§49)
            cancellationToken);

        logger.LogInformation("Daily review stored for {Owner} ({Items} items).", ownerId, day.Count);
        return true;
    }
}
