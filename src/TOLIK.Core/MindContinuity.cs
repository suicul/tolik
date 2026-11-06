namespace TOLIK.Core;

/// <summary>
/// The restorable part of the mind (PLAN-ЭТАЛОН Phase 2.3 — docs/mind.md §7.4 continuity).
///
/// MindState alone is not enough: the failure streak is what makes the mind notice its own
/// trouble (MI-7 — three consecutive failures and it proactively says so), and it lives on
/// MindRuntime, not on MindState. Persisting only the snapshot would restore the mood while
/// silently resetting the self-awareness, which is the worse half of the loss.
///
/// This record is the honest, complete unit of "what the mind was" at a point in time.
/// </summary>
public sealed record MindContinuityRecord(
    MindSnapshot Snapshot,
    int ConsecutiveFailures,
    bool FailurePatternSurfaced,
    DateTimeOffset? LastInteraction,
    double Uncertainty);
