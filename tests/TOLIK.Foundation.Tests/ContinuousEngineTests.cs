using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// CA-7 (docs/continuous-autonomy.md — PLAN REFORM PRG-3c): the continuous autonomy
/// engine — the ASSEMBLED loop: the mind exists (1 Hz), feels the real events, chooses
/// its occupations, tracks goals, arbitrates attention, and the supervisor watches the
/// tasks. NEW in v1: the SystemReview occupation EXECUTES — the deterministic system
/// review is computed and journaled (the occupation produces a real result, never a
/// pose). The honest boundary: the task-CREATING loop (the mind → the planner → the
/// authorization) awaits the governance E2/F — the plan's own dependency.
/// </summary>
public sealed class ContinuousEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SystemReview_occupation_executes_and_journals_the_review()
    {
        var mind = new MindRuntime();
        // LOCKED: the catalog restricts to the safety-relevant candidates; the Safety
        // drive spikes to 1.0 → SystemReview beats the Idle baseline decisively.
        mind.MarkSecurityState(true);
        for (var i = 0; i < 35; i++) mind.Tick(T0.AddSeconds(i)); // 30+ ticks → the selector runs
        var entries = mind.Journal.Recent(32);
        Assert.Contains(entries, e => e.Kind == MindJournalKind.ActivitySelected);
        var selected = entries.First(e => e.Kind == MindJournalKind.ActivitySelected);
        Assert.Equal(ActivityCandidate.SystemReview.Summary, selected.Summary); // the occupation chosen
        // THE OCCUPATION PRODUCED: the review result is journaled with the deterministic statistics.
        Assert.Contains(entries, e => e.Kind == MindJournalKind.ReviewResult);
        var review = entries.First(e => e.Kind == MindJournalKind.ReviewResult);
        Assert.Contains("исходов", review.Summary); // the real ReflectionSummary output
    }

    [Fact]
    public void Idle_occupation_does_not_fake_a_review()
    {
        var mind = new MindRuntime();
        mind.MarkSecurityState(false);
        for (var i = 0; i < 35; i++) mind.Tick(T0.AddSeconds(i));
        var entries = mind.Journal.Recent(32);
        // Whatever the occupation was, a review exists ONLY if SystemReview was actually
        // selected — the other occupations never fake one (§50: never a pose).
        var hasReview = entries.Any(e => e.Kind == MindJournalKind.ReviewResult);
        var selectedSystemReview = entries.Any(e => e.Kind == MindJournalKind.ActivitySelected && e.Summary == ActivityCandidate.SystemReview.Summary);
        Assert.Equal(selectedSystemReview, hasReview);
    }

    [Fact]
    public void The_engine_assembles_the_full_loop()
    {
        // The real events flow → the mind feels → the occupation chosen → the goal
        // tracked → the attention arbitrated — the continuous engine's observable loop.
        var mind = new MindRuntime();
        mind.OnTaskStarted();
        mind.OnTaskFailed();
        mind.MarkInteraction(T0);
        for (var i = 0; i < 35; i++) mind.Tick(T0.AddSeconds(i));
        var entries = mind.Journal.Recent(32);
        Assert.Contains(entries, e => e.Kind == MindJournalKind.TaskFailed);        // the real event registered
        Assert.Contains(entries, e => e.Kind == MindJournalKind.ActivitySelected);  // the mind chose an occupation
        Assert.NotNull(mind.Snapshot().AttentionTarget);                            // the attention arbitrated
        var mood = mind.MoodSnapshot();
        Assert.InRange(mood.Valence, -1.0, 1.0);                                    // the mood layer alive
        // The honest boundary (documented): the Investigation/TaskFollowUp occupations
        // journal + track goals; the task-CREATING loop awaits the governance E2/F.
    }
}
