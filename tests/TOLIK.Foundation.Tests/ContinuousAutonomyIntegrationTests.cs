using System.Reflection;
using TOLIK.Contracts;
using TOLIK.Core;
using TOLIK.Infrastructure.Authorization;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// THE CAPSTONE INTEGRATION TEST (PLAN REFORM PRG-1..5): the full continuous
/// autonomy loop — the real events flow in, the mind feels them, the goals track,
/// the mood evolves, the journal records, the proactive policy decides, the
/// rewards are earned honestly, the recipes blend, and the expression state
/// carries ALL of it — with the STRUCTURAL guarantees verified at every layer.
/// </summary>
public sealed class ContinuousAutonomyIntegrationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_full_loop_works_end_to_end()
    {
        var mind = new MindRuntime();
        Assert.Equal(SeedStatus.NotSeeded, mind.GenesisSeed.Status);
        Assert.Equal(0, mind.ExpressionEmotions(T0).Uncertainty); // honest zeros — nothing happened

        mind.OnTaskStarted();
        mind.MarkInteraction(T0);
        mind.OnTaskFailed();
        var afterFailure = mind.Snapshot();
        Assert.True(afterFailure.Frustration > 0);

        for (var i = 0; i < 20; i++) mind.Tick(T0.AddSeconds(i));
        // The task FAILED → not FOCUSED anymore (the honest activity level reflects the actual state).
        Assert.NotEqual("FOCUSED", mind.Snapshot().ActivityLevel);
        Assert.True(mind.Snapshot().Frustration > 0); // the frustration from the failure persists
        var entries = mind.Journal.Recent(32);
        Assert.Contains(entries, e => e.Kind == MindJournalKind.TaskStarted);
        Assert.Contains(entries, e => e.Kind == MindJournalKind.Interaction);
        Assert.Contains(entries, e => e.Kind == MindJournalKind.TaskFailed);

        for (var i = 20; i < 40; i++) mind.Tick(T0.AddSeconds(i));
        Assert.NotEmpty(mind.Journal.Recent(32).Where(e => e.Kind == MindJournalKind.ActivitySelected));

        var mood = mind.MoodSnapshot();
        Assert.InRange(mood.Valence, -1.0, 1.0);
        Assert.InRange(mood.Arousal, 0.0, 1.0);
        Assert.InRange(mood.Momentum, -1.0, 1.0);

        var reflection = mind.ReflectionSummary(TimeSpan.FromHours(1), T0.AddMinutes(1));
        Assert.Contains("провалено 1", reflection);
    }

    [Fact]
    public void Rewards_are_structurally_separate_from_authorization()
    {
        var rewardStore = new RewardStore();
        rewardStore.Record(RewardType.Achievement, "Задача завершена", "system:task_completed", T0);
        var methods = typeof(RewardStore).GetMethods()
            .Where(m => m.DeclaringType == typeof(RewardStore));
        Assert.All(methods, m => Assert.NotEqual(typeof(AutonomyDecision), m.ReturnType));
        var contextProps = typeof(AutonomyContext).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(contextProps, p => p.Contains("Reward", StringComparison.OrdinalIgnoreCase));
        var emotionNames = new[] { "Mood", "Emotion", "Frustration", "Satisfaction", "Valence", "Arousal" };
        var actualProps = typeof(AutonomyContext).GetProperties().Select(p => p.Name).ToList();
        foreach (var emotion in emotionNames)
            Assert.DoesNotContain(emotion, actualProps);
    }

    [Fact]
    public void Recipes_never_bypass_the_authorization()
    {
        var contextPropertyTypes = typeof(AutonomyContext).GetProperties()
            .Select(p => p.PropertyType).ToList();
        Assert.DoesNotContain(contextPropertyTypes, t => t == typeof(ExpressionState));
        Assert.DoesNotContain(contextPropertyTypes, t => t == typeof(ExpressionRecipe));
    }

    [Fact]
    public void Quiet_window_and_budget_gate_proactive_output()
    {
        var mind = new MindRuntime();
        mind.MarkInteraction(T0);
        var quiet = ProactivePolicy.Decide(ProactivePolicy.ProactiveKind.SystemSignal,
            mind.Snapshot(), minutesSinceInteraction: 1.0, proactivesToday: 0, now: T0.AddMinutes(2), locked: false);
        Assert.False(quiet.Allowed); // the quiet window blocks
        var later = ProactivePolicy.Decide(ProactivePolicy.ProactiveKind.SystemSignal,
            mind.Snapshot(), minutesSinceInteraction: 30.0, proactivesToday: 0, now: T0.AddMinutes(30), locked: false);
        Assert.True(later.Allowed); // after the quiet window — the system signal reaches
    }
}
