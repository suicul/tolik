using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Mind core (docs/mind.md MI-0..2, pre-Genesis): the MindState subsystems (deterministic
/// updates, clamped), the Boredom as a REAL mechanism (continuous from measurable inputs,
/// never a timer), the novelty/curiosity models, and the internal event bus (event-driven,
/// never while(true) askLLM; non-blocking, bounded).
/// </summary>
public sealed class MindStateTests
{
    [Fact]
    public void Subsystem_adjustments_are_clamped_bounded()
    {
        var mind = new MindState();
        mind.Adjust("boredom", 0.3);
        Assert.Equal(0.3, mind.Boredom, 6);
        mind.Adjust("boredom", 5.0); // overflow clamps to 1
        Assert.Equal(1.0, mind.Boredom, 6);
        mind.Adjust("boredom", -5.0); // underflow clamps to 0
        Assert.Equal(0.0, mind.Boredom, 6);
        mind.Adjust("novelty", 0.5);
        Assert.Equal(0.5, mind.Novelty, 6);
    }

    [Fact]
    public void Unknown_subsystem_fails_closed()
    {
        var mind = new MindState();
        Assert.Throws<ArgumentException>(() => mind.Adjust("love", 0.5));
        Assert.Throws<ArgumentException>(() => mind.Adjust("", 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => mind.Adjust("boredom", double.NaN));
    }

    [Fact]
    public void Activity_level_unknown_fails_closed_to_rest()
    {
        var mind = new MindState();
        mind.SetActivityLevel("FOCUSED");
        Assert.Equal("FOCUSED", mind.ActivityLevel);
        mind.SetActivityLevel("party");
        Assert.Equal("REST", mind.ActivityLevel); // fail closed: idle is a valid result (mind §8)
        mind.SetActivityLevel(null);
        Assert.Equal("REST", mind.ActivityLevel);
    }

    [Fact]
    public void Attention_target_is_routing_only()
    {
        var mind = new MindState();
        mind.SetAttention("voice pipeline");
        Assert.Equal("voice pipeline", mind.AttentionTarget);
        mind.SetAttention(null);
        Assert.Null(mind.AttentionTarget);
    }

    [Fact]
    public void Snapshot_is_versioned_and_complete()
    {
        var mind = new MindState();
        mind.Adjust("curiosity", 0.4);
        mind.SetUncertainty("не уверен, какая Мария");
        var snapshot = mind.Snapshot();
        Assert.Equal(MindState.SnapshotVersion, snapshot.Version);
        Assert.Equal(0.4, snapshot.Curiosity, 6);
        Assert.Equal("не уверен, какая Мария", snapshot.UncertaintyNote);
        Assert.NotEqual(default, snapshot.UpdatedAt);
    }
}

public sealed class BoredomModelTests
{
    [Fact]
    public void Idle_long_inactivity_low_novelty_produces_high_boredom()
    {
        var model = new BoredomModel();
        var boredom = model.Compute(200, hasActiveTasks: false, novelty: 0.0, recentMeaningfulInteraction: false);
        Assert.Equal(1.0, boredom, 6); // strongly seeking activity
    }

    [Fact]
    public void Meaningful_interaction_and_tasks_keep_boredom_low()
    {
        var model = new BoredomModel();
        var boredom = model.Compute(0, hasActiveTasks: true, novelty: 0.9, recentMeaningfulInteraction: true);
        Assert.True(boredom < 0.1);
    }

    [Fact]
    public void Inactivity_saturates_beyond_threshold()
    {
        var model = new BoredomModel();
        var at120 = model.Compute(120, hasActiveTasks: false, novelty: 0.0, recentMeaningfulInteraction: false);
        var at600 = model.Compute(600, hasActiveTasks: false, novelty: 0.0, recentMeaningfulInteraction: false);
        Assert.Equal(at120, at600, 6); // saturating: 10 hours == 2 hours (bounded, not linear-forever)
        Assert.True(at600 > 0.9);
    }

    [Fact]
    public void Novelty_deficit_raises_boredom()
    {
        var model = new BoredomModel();
        var repetitive = model.Compute(60, hasActiveTasks: true, novelty: 0.0, recentMeaningfulInteraction: false);
        var fresh = model.Compute(60, hasActiveTasks: true, novelty: 1.0, recentMeaningfulInteraction: false);
        Assert.True(repetitive > fresh); // low novelty → more boredom
    }

    [Fact]
    public void Boredom_always_bounded()
    {
        var model = new BoredomModel();
        Assert.InRange(model.Compute(10000, false, 0.0, false), 0.0, 1.0);
        Assert.InRange(model.Compute(-50, true, 1.0, true), 0.0, 1.0);
    }
}

public sealed class NoveltyCuriosityTests
{
    [Fact]
    public void Repeats_decay_novelty_unexpected_raises_it()
    {
        var tracker = new NoveltyTracker();
        tracker.Register("repeat");
        tracker.Register("repeat");
        Assert.True(tracker.Current < 0.2);
        tracker.Register("unexpected");
        Assert.True(tracker.Current > 0.2); // the biggest rise
    }

    [Fact]
    public void New_application_raises_more_than_new_info()
    {
        var tracker = new NoveltyTracker();
        tracker.Register("new_info");
        var afterInfo = tracker.Current;
        tracker.Register("new_application");
        Assert.True(tracker.Current > afterInfo); // ↑↑ beats ↑
    }

    [Fact]
    public void Novelty_always_bounded()
    {
        var tracker = new NoveltyTracker();
        for (var i = 0; i < 100; i++) tracker.Register("unexpected");
        Assert.InRange(tracker.Current, 0.0, 1.0);
        for (var i = 0; i < 100; i++) tracker.Register("repeat");
        Assert.InRange(tracker.Current, 0.0, 1.0);
    }

    [Fact]
    public void Curiosity_triggers_raise_satisfied_lowers()
    {
        var model = new CuriosityModel();
        model.Register("unexpected");
        model.Register("contradiction");
        Assert.True(model.Intensity > 0.5);
        model.Register("satisfied");
        Assert.True(model.Intensity < 0.5); // the investigation satisfied the drive
        for (var i = 0; i < 100; i++) model.Register("unexpected");
        Assert.InRange(model.Intensity, 0.0, 1.0); // bounded
    }
}

public sealed class MindEventBusTests
{
    [Fact]
    public void Publish_never_blocks_and_reads_in_order()
    {
        var bus = new MindEventBus();
        for (var i = 0; i < 20; i++)
            Assert.True(bus.TryPublish(MindEvent.Of(MindEventKind.TimerElapsed, $"tick {i}")));
        var collected = new List<string>();
        while (bus.Reader.TryRead(out var e)) collected.Add(e.Detail!);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"tick {i}"), collected);
    }

    [Fact]
    public void Overflow_drops_oldest_bounded()
    {
        var bus = new MindEventBus();
        for (var i = 0; i < 1000; i++)
            Assert.True(bus.TryPublish(MindEvent.Of(MindEventKind.NewInformation, $"e{i}"))); // never blocks
        var collected = new List<string>();
        while (bus.Reader.TryRead(out var e)) collected.Add(e.Detail!);
        Assert.Equal(MindEventBus.QueueCapacity, collected.Count);
        Assert.Equal("e744", collected[0]); // the oldest dropped (capacity 256: e744..e999 kept)
    }

    [Fact]
    public void Event_kinds_are_fixed_literals()
    {
        // Fixed literals — never user content (§38); deterministic handlers only.
        Assert.Equal("user_spoke", MindEventKind.UserSpoke);
        Assert.Equal("boredom_threshold_crossed", MindEventKind.BoredomThresholdCrossed);
    }
}
