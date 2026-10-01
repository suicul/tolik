using Microsoft.EntityFrameworkCore;
using TOLIK.Core;

namespace TOLIK.Infrastructure.Persistence;

public sealed class DurableMemoryStore(IDbContextFactory<TolikDbContext> dbFactory)
{
    public async Task<MemoryItem> AddAsync(
        Guid ownerId,
        MemoryScope scope,
        string content,
        string source,
        double confidence,
        CancellationToken cancellationToken = default,
        string origin = MemoryOrigin.Lived)
    {
        Validate(ownerId, content, source, confidence, origin);

        var item = new MemoryItem(
            Guid.NewGuid(),
            ownerId,
            scope,
            content,
            source,
            confidence,
            DateTimeOffset.UtcNow,
            MemoryOrigin.Normalize(origin));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Memories.Add(ToEntity(item));
        await db.SaveChangesAsync(cancellationToken);
        return item;
    }

    public async Task<IReadOnlyList<MemoryItem>> SearchAsync(
        Guid requesterId,
        string query,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (requesterId == Guid.Empty)
            throw new ArgumentException("Requester is required.", nameof(requesterId));
        if (query is null)
            throw new ArgumentNullException(nameof(query));
        if (query.Length > 256)
            throw new ArgumentException("Query is too long.", nameof(query));
        if (limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var normalized = query.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var rows = await db.Memories
            .AsNoTracking()
            .Where(x =>
                (x.Scope == nameof(MemoryScope.Shared) || x.Scope == nameof(MemoryScope.Public) ||
                    (x.Scope == nameof(MemoryScope.Private) && x.OwnerId == requesterId)) &&
                (normalized.Length == 0 || EF.Functions.ILike(x.Content, $"%{EscapeLikePattern(normalized)}%", "\\")))
            .OrderByDescending(x => x.Confidence)
            .ThenByDescending(x => x.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return rows
            .Where(x => Enum.TryParse<MemoryScope>(x.Scope, ignoreCase: false, out _))
            .Select(x => new MemoryItem(x.Id, x.OwnerId, Enum.Parse<MemoryScope>(x.Scope), x.Content, x.Source, x.Confidence, x.CreatedAt, x.Origin))
            .ToArray();
    }

    public async Task<IReadOnlyList<MemoryItem>> SearchBySourceAsync(
        Guid requesterId,
        string source,
        CancellationToken cancellationToken = default)
    {
        if (requesterId == Guid.Empty) throw new ArgumentException("Requester is required.", nameof(requesterId));
        if (string.IsNullOrWhiteSpace(source) || source.Length > 256) throw new ArgumentException("Source is invalid.", nameof(source));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Memories.AsNoTracking()
            .Where(x => x.Source == source && (x.Scope != nameof(MemoryScope.Private) || x.OwnerId == requesterId))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        return rows.Where(x => Enum.TryParse<MemoryScope>(x.Scope, out _))
            .Select(x => new MemoryItem(x.Id, x.OwnerId, Enum.Parse<MemoryScope>(x.Scope), x.Content, x.Source, x.Confidence, x.CreatedAt, x.Origin))
            .ToArray();
    }

    /// <summary>Returns oldest-to-newest non-compacted memories for the owner created before the cutoff.</summary>
    public async Task<IReadOnlyList<MemoryItem>> GetOlderThanAsync(
        Guid ownerId,
        DateTimeOffset cutoff,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Memories.AsNoTracking()
            .Where(x => x.OwnerId == ownerId && x.CreatedAt < cutoff && x.CompactedAt == null)
            .OrderBy(x => x.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return rows.Where(x => Enum.TryParse<MemoryScope>(x.Scope, out _))
            .Select(x => new MemoryItem(x.Id, x.OwnerId, Enum.Parse<MemoryScope>(x.Scope), x.Content, x.Source, x.Confidence, x.CreatedAt, x.Origin))
            .ToArray();
    }

    /// <summary>Returns the day's conversational memories for the daily review (§19: episodic memory).</summary>
    public async Task<IReadOnlyList<MemoryItem>> GetDayAsync(
        Guid ownerId,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Memories.AsNoTracking()
            .Where(x => x.OwnerId == ownerId && x.CreatedAt >= since)
            .OrderBy(x => x.CreatedAt)
            .Take(500)
            .ToListAsync(cancellationToken);

        return rows.Where(x => Enum.TryParse<MemoryScope>(x.Scope, out _))
            .Select(x => new MemoryItem(x.Id, x.OwnerId, Enum.Parse<MemoryScope>(x.Scope), x.Content, x.Source, x.Confidence, x.CreatedAt, x.Origin))
            .ToArray();
    }

    /// <summary>Returns true when a memory with the given source key already exists since the timestamp (idempotency guard).</summary>
    public async Task<bool> ExistsSinceAsync(
        Guid ownerId,
        string source,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Memories.AsNoTracking()
            .AnyAsync(x => x.OwnerId == ownerId && x.Source == source && x.CreatedAt >= since, cancellationToken);
    }

    /// <summary>Marks a memory as compacted (folded into a summary). Content is preserved for auditability.</summary>
    public async Task MarkCompactedAsync(Guid memoryId, CancellationToken cancellationToken = default)
    {
        if (memoryId == Guid.Empty) throw new ArgumentException("Memory id is required.", nameof(memoryId));
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Memories.SingleOrDefaultAsync(x => x.Id == memoryId, cancellationToken);
        if (row is null || row.CompactedAt is not null) return;
        row.CompactedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Owner-only delete (§50: users inspect, correct and delete stored memory).
    /// Only the creator may delete, for every scope. Returns false for unknown ids
    /// and foreign owners alike — the endpoint maps both to 404 (no existence oracle).
    /// </summary>
    public async Task<bool> DeleteAsync(Guid requesterId, Guid memoryId, CancellationToken cancellationToken = default)
    {
        if (requesterId == Guid.Empty || memoryId == Guid.Empty) return false;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Memories.SingleOrDefaultAsync(x => x.Id == memoryId, cancellationToken);
        if (row is null || row.OwnerId != requesterId) return false;
        db.Memories.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Development reset (§23 tests, future ceremony tooling): deletes ALL memories
    /// of an owner regardless of origin. Never exposed without a lifecycle gate
    /// (Phase 4) — LIFETIME must reject this outright.
    /// </summary>
    public async Task<int> DeleteAllAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Memories.Where(x => x.OwnerId == ownerId).ToListAsync(cancellationToken);
        db.Memories.RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    private static TolikMemoryEntity ToEntity(MemoryItem item) => new()
    {
        Id = item.Id,
        OwnerId = item.OwnerId,
        Scope = item.Scope.ToString(),
        Content = item.Content,
        Source = item.Source,
        Confidence = item.Confidence,
        CreatedAt = item.CreatedAt,
        Origin = item.Origin
    };

    private static void Validate(Guid ownerId, string content, string source, double confidence, string origin)
    {
        if (ownerId == Guid.Empty || string.IsNullOrWhiteSpace(content) || content.Length > 8192 ||
            string.IsNullOrWhiteSpace(source) || source.Length > 256 || confidence is < 0 or > 1 ||
            double.IsNaN(confidence) || double.IsInfinity(confidence) || !MemoryOrigin.IsValid(origin))
            throw new ArgumentException("Invalid memory.");
    }

    private static string EscapeLikePattern(string value) => value.Replace("%", "[%]").Replace("_", "[_]");
}
