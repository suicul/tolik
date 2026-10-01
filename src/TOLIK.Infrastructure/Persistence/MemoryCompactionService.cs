using Microsoft.Extensions.Logging;
using TOLIK.Core;
using TOLIK.Infrastructure.Models;

namespace TOLIK.Infrastructure.Persistence;

/// <summary>
/// Consolidates old low-relevance memories into compact summaries.
/// Runs as a periodic background loop; never deletes Owner-scoped Private
/// memories without producing a replacement summary first.
/// The model output is treated as data: compaction never creates facts with
/// confidence higher than the summarized items (AGENTS.md §49).
/// </summary>
public sealed class MemoryCompactionService(
    DurableMemoryStore durableMemory,
    ModelRouter router,
    ModelCompletionClient completion,
    ILogger<MemoryCompactionService> logger)
{
    /// <summary>How many old memories trigger a compaction pass.</summary>
    public const int CompactionThreshold = 40;

    /// <summary>Memories older than this are eligible for compaction.</summary>
    public static readonly TimeSpan CompactionAge = TimeSpan.FromDays(3);

    /// <summary>How many memories survive compaction as-is (most recent).</summary>
    public const int KeepRecentCount = 20;

    /// <summary>
    /// Runs one compaction pass for the given owner: summarizes stale memories
    /// into a single consolidated entry and marks the originals as compacted.
    /// Returns the number of memories compacted, or 0 when nothing to do.
    /// </summary>
    public async Task<int> CompactOwnerAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - CompactionAge;
        var stale = await durableMemory.GetOlderThanAsync(ownerId, cutoff, CompactionThreshold + KeepRecentCount, cancellationToken);
        if (stale.Count < CompactionThreshold)
            return 0;

        var toCompact = stale.Skip(KeepRecentCount).Take(CompactionThreshold).ToList();
        if (toCompact.Count == 0)
            return 0;

        var route = router.Resolve(ModelTaskKind.Simple);
        if (route is null)
        {
            logger.LogWarning("Memory compaction skipped: no model route configured.");
            return 0;
        }

        var corpus = string.Join("\n", toCompact.Select((m, i) => $"{i + 1}. [{m.CreatedAt:yyyy-MM-dd}] {m.Content}"));
        var prompt =
            "Ты — система консолидации памяти личного ассистента TOLIK. " +
            "Объедини следующие воспоминания в 3-6 компактных фактов на русском языке. " +
            "Не добавляй ничего от себя. Не выдумывай факты. Сохрани даты, имена и числа. " +
            "Ответ — только список фактов, без вступления.\n\n" + corpus;

        string summary;
        try
        {
            summary = await completion.CompleteAsync(route, new ModelRequest(route.Model, prompt), TimeSpan.FromSeconds(90), cancellationToken);
        }
        catch (ModelProviderUnavailableException ex)
        {
            logger.LogWarning("Memory compaction skipped: model unavailable ({Message}).", ex.Message);
            return 0;
        }

        summary = summary.Trim();
        if (summary.Length == 0 || summary.Length > 8192)
        {
            logger.LogWarning("Memory compaction skipped: summary failed sanity bounds.");
            return 0;
        }

        // The summary inherits the LOWEST confidence of the compacted items, never higher.
        var confidence = toCompact.Min(m => m.Confidence) * 0.9;
        await durableMemory.AddAsync(
            ownerId,
            MemoryScope.Private,
            $"[Сводка памяти] {summary}",
            "memory.compaction",
            confidence,
            cancellationToken,
            origin: MemoryOrigin.System);

        foreach (var memory in toCompact)
            await durableMemory.MarkCompactedAsync(memory.Id, cancellationToken);

        logger.LogInformation("Compacted {Count} memories for owner {OwnerId}.", toCompact.Count, ownerId);
        return toCompact.Count;
    }
}
