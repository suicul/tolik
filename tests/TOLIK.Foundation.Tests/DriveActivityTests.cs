using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// DriveSystem + ActivitySelector (docs/mind.md §3/§4, MI-3 — PLAN REFORM PRG-1c):
/// the 15 drives computed deterministically from the REAL measurable inputs; the
/// selection over the FIXED candidate catalog; «просто idle — тоже правильный
/// результат»; the cooldown anti-loop; LOCKED restricts the catalog; the drives
/// create MOTIVATION, never permission (no authorization surface exists here).
/// </summary>
public sealed class DriveSystemTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static MindSnapshot Snap(double boredom = 0, double curiosity = 0, double novelty = 0,
        double satisfaction = 0, double frustration = 0, double social = 0, double load = 0, string activity = "REST") =>
        new(MindState.SnapshotVersion, boredom, curiosity, novelty, frustration, satisfaction, social, load, activity, null, null, T0);

    [Fact]
    public void All_fifteen_drives_computed_and_valid()
    {
        var drives = DriveSystem.Compute(Snap(), new MindInputs(10, 0, Locked: false));
        Assert.Equal(15, drives.Count);
        Assert.All(drives, d => Assert.True(d.IsValid, $"{d.Name} must be finite [0..1]"));
        Assert.Equal(15, drives.Select(d => d.Name).Distinct().Count()); // the exact fixed set
        Assert.Contains(drives, d => d.Name == DriveNames.Safety);
        Assert.Contains(drives, d => d.Name == DriveNames.ProblemSolving);
    }

    [Fact]
    public void Boredom_raises_the_seeking_drives()
    {
        var engaged = DriveSystem.Compute(Sap_Snap(), new MindInputs(1, 2, false));
        var bored = DriveSystem.Compute(Snap(boredom: 0.9), new MindInputs(240, 0, false));
        Assert.True(Drive(bored, DriveNames.Exploration) > Drive(engaged, DriveNames.Exploration));
        Assert.True(Drive(bored, DriveNames.Learning) > Drive(engaged, DriveNames.Learning));
        Assert.True(Drive(bored, DriveNames.Creativity) > Drive(engaged, DriveNames.Creativity));

        static MindSnapshot Sap_Snap() => Snap(satisfaction: 0.3);
    }

    [Fact]
    public void Interaction_drive_decays_with_recency_and_completion_tracks_tasks()
    {
        var fresh = DriveSystem.Compute(Sap_Snap(), new MindInputs(1, 0, false));
        var stale = DriveSystem.Compute(Sap_Snap(), new MindInputs(45, 0, false));
        Assert.True(Drive(fresh, DriveNames.Interaction) > Drive(stale, DriveNames.Interaction));

        var working = DriveSystem.Compute(Sap_Snap(), new MindInputs(1, 3, false));
        var idle = DriveSystem.Compute(Sap_Snap(), new MindInputs(1, 0, false));
        Assert.True(Drive(working, DriveNames.Completion) > 0.5);
        Assert.Equal(0.0, Drive(idle, DriveNames.Completion), 6); // no tasks — no completion pull

        static MindSnapshot Sap_Snap() => Snap();
    }

    [Fact]
    public void Locked_spikes_the_safety_drive()
    {
        var calm = DriveSystem.Compute(Snap(), new MindInputs(10, 0, Locked: false));
        var locked = DriveSystem.Compute(Snap(), new MindInputs(10, 0, Locked: true));
        Assert.Equal(1.0, Drive(locked, DriveNames.Safety), 6);   // the safety drive dominates
        Assert.True(Drive(calm, DriveNames.Safety) < 1.0);
    }

    [Fact]
    public void Garbage_snapshot_never_corrupts_the_drives()
    {
        var garbage = new MindSnapshot(MindState.SnapshotVersion,
            double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.NaN,
            double.NaN, double.NaN, double.NaN, "chaos", null, null, T0);
        var drives = DriveSystem.Compute(garbage, new MindInputs(double.NaN, -5, true));
        Assert.All(drives, d => Assert.True(d.IsValid)); // §18: no NaN ever leaks
    }

    private static double Drive(IReadOnlyList<DriveState> drives, string name) =>
        drives.First(d => d.Name == name).Intensity;
}

public sealed class ActivitySelectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<DriveState> Drives(params (string Name, double Value)[] set) =>
        set.Select(d => new DriveState(d.Name, d.Value)).ToList();

    [Fact]
    public void Flat_drives_select_idle_the_valid_result()
    {
        var selector = new ActivitySelector();
        var selection = selector.Select(Drives(), T0, locked: false);
        Assert.Equal(ActivityNames.Idle, selection.Activity); // «просто idle — тоже правильный результат»
        Assert.True(selection.IsValid);
    }

    [Fact]
    public void High_curiosity_selects_investigation()
    {
        var selector = new ActivitySelector();
        var selection = selector.Select(Drives(
            (DriveNames.Curiosity, 0.9), (DriveNames.Novelty, 0.7), (DriveNames.Exploration, 0.6)), T0, locked: false);
        Assert.Equal(ActivityNames.Investigation, selection.Activity);
    }

    [Fact]
    public void Active_tasks_pull_the_follow_up()
    {
        var selector = new ActivitySelector();
        var selection = selector.Select(Drives(
            (DriveNames.Completion, 0.9), (DriveNames.ProblemSolving, 0.8)), T0, locked: false);
        Assert.Equal(ActivityNames.TaskFollowUp, selection.Activity);
    }

    [Fact]
    public void Cooldown_prevents_re_selecting_the_same_activity()
    {
        var selector = new ActivitySelector();
        var drives = Drives((DriveNames.Curiosity, 0.9), (DriveNames.Novelty, 0.7), (DriveNames.Exploration, 0.6));
        var first = selector.Select(drives, T0, locked: false);
        Assert.Equal(ActivityNames.Investigation, first.Activity);
        // Within the cooldown the same candidate is suppressed — idle or another wins.
        var second = selector.Select(drives, T0.AddSeconds(30), locked: false);
        Assert.NotEqual(ActivityNames.Investigation, second.Activity);
        // After the cooldown expires the candidate is available again.
        var later = selector.Select(drives, T0.Add(selector.Cooldown).AddSeconds(1), locked: false);
        Assert.Equal(ActivityNames.Investigation, later.Activity);
    }

    [Fact]
    public void Locked_restricts_the_catalog_to_safety_relevant_candidates()
    {
        var selector = new ActivitySelector();
        var curious = Drives((DriveNames.Curiosity, 1.0), (DriveNames.Novelty, 1.0), (DriveNames.Exploration, 1.0));
        var selection = selector.Select(curious, T0, locked: true);
        Assert.NotEqual(ActivityNames.Investigation, selection.Activity); // no wandering while LOCKED
        Assert.True(selection.Activity is ActivityNames.Idle or ActivityNames.SystemReview);

        // But the maintenance drives CAN pick SystemReview while locked (the safety-relevant one).
        var maintenance = Drives((DriveNames.Maintenance, 0.9), (DriveNames.Safety, 0.9), (DriveNames.Order, 0.8));
        var review = selector.Select(maintenance, T0.AddMinutes(30), locked: true);
        Assert.Equal(ActivityNames.SystemReview, review.Activity);
    }

    [Fact]
    public void Deterministic_selection_for_the_same_inputs()
    {
        var a = new ActivitySelector();
        var b = new ActivitySelector();
        var drives = Drives((DriveNames.Order, 0.8), (DriveNames.Learning, 0.7));
        Assert.Equal(a.Select(drives, T0, false).Activity, b.Select(drives, T0, false).Activity);
    }
}

public sealed class MindActivityIntegrationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecordActivity_accepts_only_the_fixed_catalog_summaries()
    {
        var journal = new MindJournal();
        Assert.Throws<ArgumentException>(() => journal.RecordActivity("Смотрю порно")); // user content — NEVER
        journal.RecordActivity(ActivityCandidate.Investigation.Summary); // the fixed catalog literal ✓
        Assert.Equal(MindJournalKind.ActivitySelected, journal.Recent(1)[0].Kind);
    }

    [Fact]
    public void MindRuntime_selects_and_journals_activity_transitions()
    {
        var mind = new MindRuntime();
        mind.MarkSecurityState(false);
        // 30+ ticks with a failed task (frustration + curiosity up) → a selection happens.
        mind.OnTaskStarted();
        mind.OnTaskFailed();
        for (var i = 0; i < 35; i++) mind.Tick(T0.AddSeconds(i));
        var entries = mind.Journal.Recent(32);
        // The task events + (at least) one ActivitySelected transition are journaled.
        Assert.Contains(entries, e => e.Kind == MindJournalKind.TaskFailed);
        Assert.Contains(entries, e => e.Kind == MindJournalKind.ActivitySelected);
        var snapshot = mind.Snapshot();
        Assert.NotNull(snapshot.AttentionTarget); // the activity became the attention target (routing only)
    }

    [Fact]
    public void Journal_renders_the_activity_summary_for_the_owner()
    {
        var journal = new MindJournal();
        journal.RecordActivity(ActivityCandidate.SystemReview.Summary, T0);
        var summary = journal.RenderRecent(TimeSpan.FromHours(1), T0.AddMinutes(1));
        Assert.Contains(ActivityCandidate.SystemReview.Summary, summary);
    }
}
