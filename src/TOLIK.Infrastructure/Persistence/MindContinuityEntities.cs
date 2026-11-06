namespace TOLIK.Infrastructure.Persistence;

/// <summary>
/// The persisted mind snapshot (PLAN-ЭТАЛОН Phase 2.3 — docs/mind.md §7.4).
///
/// Singleton by construction: Id is fixed at 1, mirroring CoreLeaseEntity. The mind has ONE
/// current state, not a history — a second row would be a second truth about the same mind.
/// The full history of how the mind got here is the audit chain's job, not this table's.
/// </summary>
public sealed class MindSnapshotEntity
{
    /// <summary>The singleton sentinel — at most one row may ever exist.</summary>
    public const long SingletonId = 1;

    public long Id { get; set; } = SingletonId;

    /// <summary>The MindState snapshot version this row was written with. A row from an older
    /// version is refused at load time rather than silently reinterpreted.</summary>
    public int Version { get; set; }

    public double Boredom { get; set; }
    public double Curiosity { get; set; }
    public double Novelty { get; set; }
    public double Frustration { get; set; }
    public double Satisfaction { get; set; }
    public double SocialDrive { get; set; }
    public double CognitiveLoad { get; set; }
    public string ActivityLevel { get; set; } = "REST";
    public string? AttentionTarget { get; set; }
    public string? UncertaintyNote { get; set; }

    /// <summary>When the mind last changed (from the snapshot itself).</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>MI-7 self-awareness: the consecutive-failure streak and whether it was already surfaced.</summary>
    public int ConsecutiveFailures { get; set; }
    public bool FailurePatternSurfaced { get; set; }

    /// <summary>The last meaningful interaction, or null if there has never been one.</summary>
    public DateTimeOffset? LastInteraction { get; set; }

    /// <summary>The MI-4 explicit uncertainty level.</summary>
    public double Uncertainty { get; set; }

    /// <summary>When this row was written (distinct from UpdatedAt: a restart that changes
    /// nothing still moves this clock, and that difference is diagnostic).</summary>
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>
/// The persisted Genesis seed (PLAN-ЭТАЛОН Phase 2.2/2.3 — docs/mind.md MI-9).
///
/// Both halves are stored: the seed DATA and the ACTIVATION flag. Storing only the data
/// would let a restart quietly demote a born identity back to "Seeded", which is the same
/// class of bug as losing MindState — the mind would have its personality but not the right
/// to read it. Singleton, same reasoning as MindSnapshotEntity.
/// </summary>
public sealed class GenesisSeedEntity
{
    public const long SingletonId = 1;

    public long Id { get; set; } = SingletonId;
    public string Temperament { get; set; } = string.Empty;

    /// <summary>The core values, one per row in the child table — not a delimited string,
    /// so a value containing the delimiter can never be silently split.</summary>
    public List<GenesisSeedValueEntity> CoreValues { get; set; } = [];

    public DateTimeOffset SeededAt { get; set; }
    public bool Activated { get; set; }
}

/// <summary>One core value of the Genesis seed (ordered — the order is part of the seed).</summary>
public sealed class GenesisSeedValueEntity
{
    public long Id { get; set; }
    public long SeedId { get; set; }
    public int Ordinal { get; set; }
    public string Value { get; set; } = string.Empty;
    public GenesisSeedEntity? Seed { get; set; }
}
