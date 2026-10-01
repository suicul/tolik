using Microsoft.EntityFrameworkCore;
using TOLIK.Core;

namespace TOLIK.Infrastructure.Persistence;

/// <summary>
/// Durable learned-rules store (AGENTS.md §21).
/// Rules always carry provenance (source) and confidence; feedback-derived rules
/// start with moderate confidence and are never treated as facts.
/// </summary>
public sealed class LearnedRuleStore(IDbContextFactory<TolikDbContext> dbFactory)
{
    public async Task<LearnedRule> AddAsync(
        Guid ownerId,
        string rule,
        double confidence,
        string source,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        if (string.IsNullOrWhiteSpace(rule) || rule.Length > 8192) throw new ArgumentException("Rule is invalid.", nameof(rule));
        if (confidence is < 0 or > 1 || double.IsNaN(confidence)) throw new ArgumentException("Confidence is invalid.", nameof(confidence));
        if (string.IsNullOrWhiteSpace(source) || source.Length > 256) throw new ArgumentException("Source is required.", nameof(source));

        var entity = new TolikLearnedRuleEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Rule = rule.Trim(),
            Confidence = confidence,
            Source = source.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.LearnedRules.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new LearnedRule(entity.Id, entity.OwnerId, entity.Rule, entity.Confidence, entity.Source, entity.CreatedAt);
    }

    public async Task<IReadOnlyList<LearnedRule>> GetTopAsync(
        Guid ownerId,
        int limit = 16,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.LearnedRules.AsNoTracking()
            .Where(x => x.OwnerId == ownerId && x.Confidence >= 0.5)
            .OrderByDescending(x => x.Confidence)
            .ThenByDescending(x => x.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return rows.Select(x => new LearnedRule(x.Id, x.OwnerId, x.Rule, x.Confidence, x.Source, x.CreatedAt)).ToArray();
    }

    /// <summary>
    /// Finds an identical rule for repeat-feedback dedupe (§21: repeated explicit
    /// feedback strengthens a rule instead of duplicating it).
    /// </summary>
    public async Task<LearnedRule?> FindByTextAsync(
        Guid ownerId,
        string rule,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        if (string.IsNullOrWhiteSpace(rule)) return null;
        var normalized = rule.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.LearnedRules.AsNoTracking()
            .Where(x => x.OwnerId == ownerId && x.Rule == normalized)
            .OrderByDescending(x => x.Confidence)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null
            ? null
            : new LearnedRule(entity.Id, entity.OwnerId, entity.Rule, entity.Confidence, entity.Source, entity.CreatedAt);
    }

    /// <summary>
    /// Raises confidence on repeated explicit feedback, capped so no single
    /// interaction can mint a near-certain permanent behavior (§21).
    /// </summary>
    public async Task<LearnedRule?> BumpConfidenceAsync(
        Guid id,
        Guid ownerId,
        double delta,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("Rule id is required.", nameof(id));
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        if (delta is <= 0 or > 1 || double.IsNaN(delta)) throw new ArgumentException("Delta is invalid.", nameof(delta));
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.LearnedRules
            .SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == ownerId, cancellationToken);
        if (entity is null) return null;
        entity.Confidence = Math.Min(0.95, entity.Confidence + delta);
        await db.SaveChangesAsync(cancellationToken);
        return new LearnedRule(entity.Id, entity.OwnerId, entity.Rule, entity.Confidence, entity.Source, entity.CreatedAt);
    }
}
