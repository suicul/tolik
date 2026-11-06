using Microsoft.EntityFrameworkCore;
using TOLIK.Core;

namespace TOLIK.Infrastructure.Persistence;

/// <summary>
/// Durable mind state (PLAN-ЭТАЛОН Phase 2.3 — docs/mind.md §7.4, blocker Б3).
///
/// Before this existed, MindState was a private field of a singleton: every Core restart
/// reset boredom, curiosity, attention and the consecutive-failure streak. The mind forgot
/// that it had been struggling, which is exactly the state the owner must not lose.
///
/// One row, always. Written periodically by MindRuntimeService and once on shutdown; read
/// once at startup.
/// </summary>
public sealed class MindSnapshotStore(IDbContextFactory<TolikDbContext> dbFactory)
{
    /// <summary>Persists the current capture, replacing the previous one.</summary>
    public async Task SaveAsync(MindContinuityRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var snapshot = record.Snapshot;
        if (snapshot.Version != MindState.SnapshotVersion)
            throw new ArgumentException($"Refusing to persist snapshot version {snapshot.Version}.", nameof(record));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.MindSnapshots
            .SingleOrDefaultAsync(x => x.Id == MindSnapshotEntity.SingletonId, cancellationToken);

        if (entity is null)
        {
            entity = new MindSnapshotEntity { Id = MindSnapshotEntity.SingletonId };
            db.MindSnapshots.Add(entity);
        }

        entity.Version = snapshot.Version;
        entity.Boredom = snapshot.Boredom;
        entity.Curiosity = snapshot.Curiosity;
        entity.Novelty = snapshot.Novelty;
        entity.Frustration = snapshot.Frustration;
        entity.Satisfaction = snapshot.Satisfaction;
        entity.SocialDrive = snapshot.SocialDrive;
        entity.CognitiveLoad = snapshot.CognitiveLoad;
        entity.ActivityLevel = snapshot.ActivityLevel;
        entity.AttentionTarget = snapshot.AttentionTarget;
        entity.UncertaintyNote = snapshot.UncertaintyNote;
        entity.UpdatedAt = snapshot.UpdatedAt;
        entity.ConsecutiveFailures = record.ConsecutiveFailures;
        entity.FailurePatternSurfaced = record.FailurePatternSurfaced;
        entity.LastInteraction = record.LastInteraction;
        entity.Uncertainty = record.Uncertainty;
        entity.RecordedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the last persisted capture, or null when nothing was ever saved — an honest
    /// "nothing known" rather than an invented zero state.
    /// </summary>
    public async Task<MindContinuityRecord?> LoadLatestAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.MindSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == MindSnapshotEntity.SingletonId, cancellationToken);
        if (entity is null) return null;
        if (entity.Version != MindState.SnapshotVersion) return null; // a different meaning; do not reinterpret

        var snapshot = new MindSnapshot(
            entity.Version,
            entity.Boredom,
            entity.Curiosity,
            entity.Novelty,
            entity.Frustration,
            entity.Satisfaction,
            entity.SocialDrive,
            entity.CognitiveLoad,
            entity.ActivityLevel,
            entity.AttentionTarget,
            entity.UncertaintyNote,
            entity.UpdatedAt);

        return new MindContinuityRecord(
            snapshot,
            entity.ConsecutiveFailures,
            entity.FailurePatternSurfaced,
            entity.LastInteraction,
            entity.Uncertainty);
    }
}

/// <summary>What a persisted seed restores to: the data plus whether the ceremony already fired.</summary>
public sealed record GenesisSeedRestored(GenesisSeedData Data, bool Activated);

/// <summary>
/// Durable Genesis seed (PLAN-ЭТАЛОН Phase 2.2 — docs/mind.md MI-9).
///
/// The seed was already correct as a mechanism and dead as a feature: Load/Activate had no
/// production call sites, and both lived in memory, so even after wiring them a restart
/// would have dropped a born identity back to NotSeeded. This store is what makes the
/// birth survive the process.
/// </summary>
public sealed class GenesisSeedStore(IDbContextFactory<TolikDbContext> dbFactory)
{
    /// <summary>Writes the runtime's current seed state, creating or replacing the singleton row.</summary>
    public async Task SaveAsync(GenesisSeedRuntime seed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var data = seed.SeedDataForPersistence();
        if (data is null) throw new InvalidOperationException("There is no seed to persist.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.GenesisSeeds
            .Include(x => x.CoreValues)
            .SingleOrDefaultAsync(x => x.Id == GenesisSeedEntity.SingletonId, cancellationToken);

        if (entity is null)
        {
            entity = new GenesisSeedEntity { Id = GenesisSeedEntity.SingletonId };
            db.GenesisSeeds.Add(entity);
        }
        else
        {
            db.GenesisSeedValues.RemoveRange(entity.CoreValues);
            entity.CoreValues.Clear();
        }

        entity.Temperament = data.Temperament;
        entity.SeededAt = data.SeededAt;
        entity.Activated = seed.Status == SeedStatus.Activated;
        for (var i = 0; i < data.CoreValues.Length; i++)
            entity.CoreValues.Add(new GenesisSeedValueEntity { Ordinal = i, Value = data.CoreValues[i] });

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Reads the persisted seed, or null when the ceremony never filled one.</summary>
    public async Task<GenesisSeedRestored?> LoadLatestAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.GenesisSeeds
            .AsNoTracking()
            .Include(x => x.CoreValues)
            .SingleOrDefaultAsync(x => x.Id == GenesisSeedEntity.SingletonId, cancellationToken);
        if (entity is null) return null;

        var values = entity.CoreValues
            .OrderBy(v => v.Ordinal)
            .Select(v => v.Value)
            .ToArray();
        return new GenesisSeedRestored(
            new GenesisSeedData(entity.Temperament, values, entity.SeededAt),
            entity.Activated);
    }

    /// <summary>Applies a persisted seed to a runtime that has not yet been used.</summary>
    public void Restore(GenesisSeedRuntime runtime, GenesisSeedRestored restored)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(restored);
        runtime.Load(restored.Data);
        if (restored.Activated) runtime.Activate();
    }
}
