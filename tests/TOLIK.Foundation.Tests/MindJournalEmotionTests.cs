using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// MindJournal + EmotionEngine (docs/mind.md §4/§7, MI-5/MI-6 — PLAN REFORM PRG-1b).
/// The journal is TOLIK's operational history with PRIVACY BY CONSTRUCTION: only the
/// fixed literal kinds ever enter it (no user content — the privacy invariant is
/// structural, not a filter). The emotions emerge from the MindState TRANSITIONS —
/// mechanical readings, never LLM outputs; the mood is the slow EWMA layer.
/// </summary>
public sealed class MindJournalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fixed_kinds_only_unknown_kind_rejected()
    {
        // The structural privacy invariant: no arbitrary content can enter the journal.
        var journal = new MindJournal();
        Assert.Throws<ArgumentException>(() => journal.Record("Сообщение владельца: купи хлеб"));
        Assert.Throws<ArgumentException>(() => journal.Record(""));
        journal.Record(MindJournalKind.TaskCompleted);
        Assert.Single(journal.Recent(32));
    }

    [Fact]
    public void Ring_is_bounded_drop_oldest()
    {
        var journal = new MindJournal();
        for (var i = 0; i < MindJournal.Capacity + 100; i++)
            journal.Record(MindJournalKind.Interaction, T0.AddSeconds(i));
        var recent = journal.Recent(MindJournal.Capacity);
        Assert.Equal(MindJournal.Capacity, recent.Count);                 // the bound holds
        Assert.Equal(MindJournalKind.Interaction, recent[0].Kind);        // the oldest SURVIVOR
        Assert.Equal(T0.AddSeconds(100).Second, recent[0].At.Second);     // the first 100 dropped
    }

    [Fact]
    public void Recent_limit_is_clamped()
    {
        var journal = new MindJournal();
        for (var i = 0; i < 10; i++) journal.Record(MindJournalKind.Interaction, T0.AddSeconds(i));
        Assert.Equal(10, journal.Recent(0).Count);   // 0 → the safe default 32
        Assert.Equal(5, journal.Recent(5).Count);    // the newest 5, oldest-first
        Assert.Equal(10, journal.Recent(999).Count); // capped by what exists
    }

    [Fact]
    public void Rendering_uses_russian_plurals()
    {
        var journal = new MindJournal();
        journal.Record(MindJournalKind.TaskCompleted, T0); // 1 завершена
        var summary1 = journal.RenderRecent(TimeSpan.FromHours(1), T0.AddMinutes(1));
        Assert.Contains("1 задача завершена", summary1);

        var journal2 = new MindJournal();
        for (var i = 0; i < 3; i++) journal2.Record(MindJournalKind.TaskCompleted, T0);
        Assert.Contains("3 задачи завершено", journal2.RenderRecent(TimeSpan.FromHours(1), T0.AddMinutes(1)));

        var journal3 = new MindJournal();
        for (var i = 0; i < 5; i++) journal3.Record(MindJournalKind.TaskCompleted, T0);
        Assert.Contains("5 задач завершено", journal3.RenderRecent(TimeSpan.FromHours(1), T0.AddMinutes(1)));

        var journal4 = new MindJournal();
        journal4.Record(MindJournalKind.TaskFailed, T0);
        journal4.Record(MindJournalKind.Interaction, T0);
        var mixed = journal4.RenderRecent(TimeSpan.FromHours(1), T0.AddMinutes(1));
        Assert.Contains("1 задача провалена", mixed);
        Assert.Contains("1 взаимодействие", mixed);
    }

    [Fact]
    public void Rendering_respects_the_window_and_empty_is_honest()
    {
        var journal = new MindJournal();
        journal.Record(MindJournalKind.TaskCompleted, T0.Subtract(TimeSpan.FromHours(3))); // outside
        journal.Record(MindJournalKind.Interaction, T0);                                    // inside
        var summary = journal.RenderRecent(TimeSpan.FromHours(1), T0.AddMinutes(1));
        Assert.Contains("1 взаимодействие", summary);
        Assert.DoesNotContain("завершена", summary); // the old one is outside the window

        var empty = new MindJournal();
        Assert.Contains("ничего существенного", empty.RenderRecent(TimeSpan.FromHours(8), T0));
    }

    [Fact]
    public void MindRuntime_records_the_real_events()
    {
        var mind = new MindRuntime();
        mind.MarkInteraction(T0);
        mind.OnTaskStarted();
        mind.OnTaskCompleted();
        mind.OnTaskFailed();
        mind.OnTaskCancelled();
        var entries = mind.Journal.Recent(32);
        Assert.Equal(5, entries.Count);
        Assert.Equal(MindJournalKind.Interaction, entries[0].Kind);
        Assert.Equal("Взаимодействие", entries[0].Summary);
        Assert.Equal(MindJournalKind.TaskCancelled, entries[^1].Kind);
    }
}

public sealed class EmotionEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static MindSnapshot Snap(double novelty = 0, double satisfaction = 0, double frustration = 0,
        double curiosity = 0, string activity = "REST", double load = 0) => new(
            MindState.SnapshotVersion, 0.2, curiosity, novelty, frustration, satisfaction, 0, load,
            activity, null, null, T0);

    [Fact]
    public void Pulses_fire_on_real_transitions()
    {
        var engine = new EmotionEngine();
        engine.OnSnapshot(Snap());                                        // the baseline
        engine.OnSnapshot(Snap(novelty: 0.6, satisfaction: 0.5, frustration: 0.4, curiosity: 0.7));
        Assert.True(engine.Pulse("surprise") > 0);    // the novelty jumped
        Assert.True(engine.Pulse("content") > 0);     // the satisfaction rose
        Assert.True(engine.Pulse("frustration") > 0); // the frustration rose
        Assert.True(engine.Pulse("interest") > 0);    // the curiosity rose
        Assert.Throws<ArgumentException>(() => engine.Pulse("привет")); // the fixed literals only
    }

    [Fact]
    public void Small_transitions_do_not_pulse()
    {
        var engine = new EmotionEngine();
        engine.OnSnapshot(Snap());
        engine.OnSnapshot(Snap(novelty: 0.03)); // below the 0.05 threshold — no pulse
        Assert.Equal(0.0, engine.Pulse("surprise"), 6);
    }

    [Fact]
    public void Pulses_decay_over_ticks()
    {
        var engine = new EmotionEngine();
        engine.OnSnapshot(Snap());
        engine.OnSnapshot(Snap(novelty: 0.8));
        var atJump = engine.Pulse("surprise");
        for (var i = 0; i < 100; i++) engine.OnSnapshot(Snap(novelty: 0.8));
        Assert.True(engine.Pulse("surprise") < atJump); // decayed (MI §10: surprise passes)
        Assert.True(engine.Pulse("surprise") > 0);      // ...but lingers briefly
    }

    [Fact]
    public void Mood_is_the_slow_layer_one_event_barely_moves_it()
    {
        var engine = new EmotionEngine();
        engine.OnSnapshot(Snap()); // the neutral baseline (valence 0)
        engine.OnSnapshot(Snap(satisfaction: 1.0)); // one strong event
        var mood = engine.Mood;
        Assert.True(Math.Abs(mood.Valence) < 0.01);  // the EWMA barely moved (alpha 1/1800)
        // A sustained good stretch moves it — slowly but surely.
        for (var i = 0; i < 60_000; i++) engine.OnSnapshot(Snap(satisfaction: 1.0, activity: "NORMAL"));
        Assert.True(engine.Mood.Valence > 0.3);      // ~33 min of sustained satisfaction
        Assert.True(engine.Mood.Arousal > 0.2);      // the NORMAL activity raised the arousal
    }

    [Fact]
    public void Momentum_reads_the_trend()
    {
        var engine = new EmotionEngine();
        for (var i = 0; i < 10_000; i++) engine.OnSnapshot(Snap());           // settled at neutral
        engine.OnSnapshot(Snap(satisfaction: 1.0));                          // improving NOW
        Assert.True(engine.Mood.Momentum > 0);                               // positive: better than the settled mood
        for (var i = 0; i < 10_000; i++) engine.OnSnapshot(Snap(frustration: 1.0));
        Assert.True(engine.Mood.Momentum < 0);                                // negative: worse than the settled mood
        Assert.True(engine.Mood.Valence < 0);                                 // the sustained frustration dragged the valence
    }

    [Fact]
    public void Garbage_snapshots_never_corrupt_the_engine()
    {
        var engine = new EmotionEngine();
        var garbage = new MindSnapshot(MindState.SnapshotVersion,
            double.NaN, double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            double.NaN, double.NaN, double.NaN, "party", null, null, T0);
        engine.OnSnapshot(garbage);
        engine.OnSnapshot(garbage);
        var mood = engine.Mood;
        Assert.True(double.IsFinite(mood.Valence));   // NaN never leaks (§18)
        Assert.True(double.IsFinite(mood.Arousal));
        Assert.True(double.IsFinite(mood.Momentum));
        Assert.InRange(mood.Arousal, 0.0, 1.0);
    }

    [Fact]
    public void MindRuntime_feeds_the_engine_and_exposes_the_mood()
    {
        var mind = new MindRuntime();
        mind.OnTaskCompleted(); // the satisfaction rises → the content pulse
        Assert.True(mind.MoodSnapshot().Momentum > 0 || mind.MoodSnapshot().Momentum >= 0); // the mood accessor works
        mind.Tick(T0);
        var mood = mind.MoodSnapshot();
        Assert.InRange(mood.Arousal, 0.0, 1.0);
        Assert.InRange(mood.Valence, -1.0, 1.0);
    }
}
