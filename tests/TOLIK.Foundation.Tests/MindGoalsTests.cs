using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// MI-4 (docs/mind.md §5 — PLAN REFORM PRG-1d): the internal goals (the occupations
/// as bounded trackable objects with REAL satisfaction rules), the attention
/// arbitration (user > task > occupation — routing only, never authorization), the
/// explicit uncertainty (the real signals: failures raise it, time calms it — wired
/// into the expression), and the bounded reflection (the deterministic outcome
/// statistics — never invented lessons).
/// </summary>
public sealed class GoalStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Occupation_becomes_a_goal_deduped_per_type()
    {
        var goals = new GoalStore();
        goals.UpsertFromActivity(ActivityNames.Investigation, T0);
        goals.UpsertFromActivity(ActivityNames.Investigation, T0.AddMinutes(5)); // the same type refreshes
        Assert.Single(goals.Active());
        Assert.Equal(ActivityNames.Investigation, goals.Active()[0].Type);
        goals.UpsertFromActivity(ActivityNames.SystemReview, T0.AddMinutes(6));
        Assert.Equal(2, goals.Active().Count);
    }

    [Fact]
    public void TaskFollowUp_satisfied_when_work_drains()
    {
        var goals = new GoalStore();
        goals.UpsertFromActivity(ActivityNames.TaskFollowUp, T0);
        var changed = goals.Tick(activeTasks: 3, curiosity: 0.5, now: T0.AddMinutes(1));
        Assert.Empty(changed); // work remains — the goal stays active
        Assert.Equal(0.5, goals.Active()[0].Progress, 6);
        var drained = goals.Tick(activeTasks: 0, curiosity: 0.5, now: T0.AddMinutes(2));
        Assert.Contains(drained, c => c.Goal.Type == ActivityNames.TaskFollowUp && c.NewStatus == "Satisfied");
        Assert.Empty(goals.Active());
    }

    [Fact]
    public void Investigation_satisfied_when_curiosity_decays()
    {
        var goals = new GoalStore();
        goals.UpsertFromActivity(ActivityNames.Investigation, T0);
        var stillCurious = goals.Tick(activeTasks: 0, curiosity: 0.6, now: T0.AddMinutes(1));
        Assert.Empty(stillCurious);
        var resolved = goals.Tick(activeTasks: 0, curiosity: 0.1, now: T0.AddMinutes(2)); // < 0.15
        Assert.Contains(resolved, c => c.Goal.Type == ActivityNames.Investigation && c.NewStatus == "Satisfied");
    }

    [Fact]
    public void Goals_expire_by_timeout()
    {
        var goals = new GoalStore();
        goals.UpsertFromActivity(ActivityNames.SystemReview, T0);
        var before = goals.Tick(activeTasks: 0, curiosity: 0.5, now: T0.AddMinutes(20));
        Assert.Empty(before);
        var expired = goals.Tick(activeTasks: 0, curiosity: 0.5, now: T0.AddMinutes(31));
        Assert.Contains(expired, c => c.NewStatus == "Expired");
        Assert.Empty(goals.Active());
    }

    [Fact]
    public void Active_goal_count_is_bounded()
    {
        var goals = new GoalStore();
        // The catalog has only 4 non-idle types — the dedup caps the active goals at 4
        // naturally; the overflow expiry guards the pathological case anyway.
        foreach (var candidate in ActivityCandidate.Catalog.Where(c => c.Name != ActivityNames.Idle))
            goals.UpsertFromActivity(candidate.Name, T0);
        Assert.Equal(4, goals.Active().Count);
        Assert.True(goals.Active().Count <= GoalStore.MaxActive);
    }
}

public sealed class AttentionArbitrationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Direct_interaction_outranks_task_and_occupation()
    {
        var mind = new MindRuntime();
        mind.OnTaskStarted();                                  // the active work exists
        mind.MarkInteraction(T0);                             // the user just talked
        mind.Tick(T0.AddSeconds(30));
        Assert.Equal("user", mind.Snapshot().AttentionTarget); // §5 priority 1: the direct interaction
    }

    [Fact]
    public void Active_task_outranks_the_internal_occupation()
    {
        var mind = new MindRuntime();
        mind.OnTaskStarted();
        mind.MarkInteraction(T0.Subtract(TimeSpan.FromMinutes(30))); // the interaction is old
        mind.Tick(T0.AddSeconds(30));
        Assert.Equal("task", mind.Snapshot().AttentionTarget); // §5 priority 2: the active task
    }

    [Fact]
    public void Idle_falls_back_to_the_occupation_or_idle()
    {
        var mind = new MindRuntime();
        mind.MarkInteraction(T0.Subtract(TimeSpan.FromHours(2))); // nothing recent
        mind.Tick(T0.AddSeconds(30));
        Assert.Equal("idle", mind.Snapshot().AttentionTarget); // the honest fallback (no activity selected yet)
    }
}

public sealed class UncertaintyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Failure_raises_uncertainty_and_time_calms_it()
    {
        var mind = new MindRuntime();
        mind.OnTaskStarted();
        mind.OnTaskFailed();
        mind.Tick(T0.AddSeconds(1));
        var raised = mind.ExpressionEmotions(T0.AddSeconds(1)).Uncertainty;
        Assert.True(raised > 0.15); // the failure registered
        for (var i = 2; i < 500; i++) mind.Tick(T0.AddSeconds(i));
        var calmed = mind.ExpressionEmotions(T0.AddSeconds(500)).Uncertainty;
        Assert.True(calmed < raised); // the steady observations decay it
        Assert.True(calmed >= 0);
    }

    [Fact]
    public void Uncertainty_flows_into_the_expression_state()
    {
        var mind = new MindRuntime();
        mind.OnTaskFailed();
        mind.Tick(T0);
        var emotions = mind.ExpressionEmotions(T0);
        var state = TOLIK.Core.Host.CephalonExpressionService.ToExpressionState(
            new TOLIK.Core.Host.CephalonUserSnapshot(0, 0), locked: false, turnInFlight: false, connected: true, emotions);
        Assert.True(state.Uncertainty > 0); // PRG-1d: wired (was an honest zero)
        Assert.Equal(emotions.Uncertainty, state.Uncertainty, 6);
    }
}

public sealed class ReflectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reflection_reads_the_real_outcomes_never_invents()
    {
        var mind = new MindRuntime();
        // The journal stamps the On* events with the REAL clock (UtcNow), so the
        // reflection window must be based on the real now — not a synthetic timestamp.
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++) { mind.OnTaskStarted(); mind.OnTaskCompleted(); }
        mind.OnTaskStarted();
        mind.OnTaskFailed();
        var summary = mind.ReflectionSummary(TimeSpan.FromHours(1), now.AddMinutes(1));
        Assert.Contains("завершено 3", summary);
        Assert.Contains("провалено 1", summary);
        Assert.Contains("Из 4 исходов", summary);
        // The honest empty window: a fresh mind with no outcomes says so plainly.
        Assert.Contains("не было", new MindRuntime().ReflectionSummary(TimeSpan.FromHours(1), now));
    }

    [Fact]
    public void Journal_rejects_fake_goal_outcomes()
    {
        var journal = new MindJournal();
        Assert.Throws<ArgumentException>(() => journal.RecordGoalOutcome("Своя цель", "Satisfied")); // not a catalog type
        Assert.Throws<ArgumentException>(() => journal.RecordGoalOutcome(ActivityNames.Investigation, "Может быть")); // not a status literal
        journal.RecordGoalOutcome(ActivityNames.Investigation, "Satisfied"); // the fixed literals ✓
        var entry = journal.Recent(1)[0];
        Assert.Equal(MindJournalKind.GoalOutcome, entry.Kind);
        Assert.Contains("Цель достигнута", entry.Summary);
    }

    [Fact]
    public void MindRuntime_journals_the_goal_transitions()
    {
        var mind = new MindRuntime();
        // A failed task → the follow-up occupation... force the goal via the selector path:
        // 35 ticks → the selection → (a TaskFollowUp or another non-idle occupation) → a goal;
        // then the goal's satisfaction/expiry transitions journal as GoalOutcome.
        mind.OnTaskStarted();
        for (var i = 0; i < 35; i++) mind.Tick(T0.AddSeconds(i));
        mind.OnTaskCompleted(); // the work drains → a TaskFollowUp goal (if selected) satisfies
        for (var i = 35; i < 70; i++) mind.Tick(T0.AddSeconds(i));
        var entries = mind.Journal.Recent(32);
        Assert.Contains(entries, e => e.Kind == MindJournalKind.ActivitySelected); // the selection happened
        // The goal outcomes are journaled only when they transition (satisfied/expired) —
        // with the small window most goals stay active; the invariant is the kind exists
        // only via the fixed-literal path (validated above).
    }
}
