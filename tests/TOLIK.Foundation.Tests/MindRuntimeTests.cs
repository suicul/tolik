using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// MindRuntime (docs/mind.md MI-0..2 + PLAN REFORM PRG-1a): TOLIK's ONE running mind
/// wired to REAL measurable inputs. Deterministic (explicit timestamps), thread-safe,
/// never fabricates (unmapped signals stay honest zeros). The mind creates MOTIVATION,
/// never permission — there is no authorization surface in this class at all.
/// </summary>
public sealed class MindRuntimeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Recent_interaction_and_active_tasks_keep_boredom_low()
    {
        var mind = new MindRuntime();
        mind.MarkInteraction(T0);
        mind.OnTaskStarted();
        mind.Tick(T0.AddMinutes(1));
        var snapshot = mind.Snapshot();
        Assert.True(snapshot.Boredom < 0.3); // engaged: recent interaction + active work
        Assert.Equal("FOCUSED", snapshot.ActivityLevel);
        Assert.True(snapshot.CognitiveLoad > 0); // the task raised the load
    }

    [Fact]
    public void Long_inactivity_without_tasks_grows_boredom()
    {
        var mind = new MindRuntime();
        mind.MarkInteraction(T0);
        mind.Tick(T0.AddMinutes(3)); // the interaction fades
        var early = mind.Snapshot().Boredom;
        mind.Tick(T0.AddMinutes(200)); // saturated inactivity + idle
        var bored = mind.Snapshot().Boredom;
        Assert.True(bored > early);
        Assert.True(bored > 0.6); // strongly seeking activity (MI-2: never a timer — the conditions)
        Assert.Equal("REST", mind.Snapshot().ActivityLevel); // idle is a valid result (mind §8)
    }

    [Fact]
    public void Task_outcomes_drive_satisfaction_and_frustration()
    {
        var mind = new MindRuntime();
        mind.OnTaskCompleted();
        Assert.True(mind.Snapshot().Satisfaction > 0); // a real success — cautious growth
        var mind2 = new MindRuntime();
        mind2.OnTaskStarted();
        mind2.OnTaskFailed();
        var snapshot = mind2.Snapshot();
        Assert.True(snapshot.Frustration > 0); // a real failure registers
        Assert.True(snapshot.Curiosity > 0);    // ...and raises the investigation drive
    }

    [Fact]
    public void Short_term_reactions_decay_over_ticks()
    {
        var mind = new MindRuntime();
        mind.OnTaskFailed();
        var atFailure = mind.Snapshot().Frustration;
        for (var i = 0; i < 100; i++) mind.Tick(T0.AddSeconds(i)); // ~100 s
        Assert.True(mind.Snapshot().Frustration < atFailure); // MI §10: surprise passes
        Assert.True(mind.Snapshot().Frustration > 0);          // ...but lingers (slow decay)
    }

    [Fact]
    public void Active_task_count_never_goes_negative()
    {
        var mind = new MindRuntime();
        mind.OnTaskCancelled(); // no task ever started — the count clamps at 0
        mind.OnTaskFailed();
        mind.OnTaskCompleted();
        Assert.True(mind.Snapshot().ActivityLevel is "REST" or "LOW"); // honest: no active work
    }

    [Fact]
    public void Expression_emotions_flow_and_attention_decays()
    {
        var mind = new MindRuntime();
        mind.OnTaskStarted();
        mind.OnTaskCompleted();
        mind.MarkInteraction(T0);
        var now = T0.AddMinutes(1);
        var engaged = mind.ExpressionEmotions(now);
        Assert.True(engaged.Attention > 0.8); // just interacted — full attention
        Assert.True(engaged.Confidence > 0);  // the completed task earned satisfaction
        var later = mind.ExpressionEmotions(T0.AddMinutes(12));
        Assert.Equal(0.0, later.Attention);   // the focus decayed to honest 0
    }

    [Fact]
    public void Snapshot_is_versioned_and_consistent()
    {
        var mind = new MindRuntime();
        mind.MarkInteraction(T0);
        mind.Tick(T0);
        var snapshot = mind.Snapshot();
        Assert.Equal(MindState.SnapshotVersion, snapshot.Version);
        Assert.InRange(snapshot.Boredom, 0.0, 1.0);
        Assert.InRange(snapshot.Curiosity, 0.0, 1.0);
        Assert.NotEqual(default, snapshot.UpdatedAt);
    }

    [Fact]
    public void Concurrent_use_is_thread_safe()
    {
        var mind = new MindRuntime();
        Parallel.For(0, 200, i =>
        {
            if (i % 3 == 0) mind.MarkInteraction(T0.AddSeconds(i));
            else if (i % 3 == 1) mind.OnTaskStarted();
            else mind.Tick(T0.AddSeconds(i));
        });
        var snapshot = mind.Snapshot();
        Assert.InRange(snapshot.Boredom, 0.0, 1.0); // no corruption, no NaN
        Assert.InRange(snapshot.SocialDrive, 0.0, 1.0);
    }
}
