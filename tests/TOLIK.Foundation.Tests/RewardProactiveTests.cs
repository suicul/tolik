using TOLIK.Core;
using TOLIK.Infrastructure.Authorization;
using TOLIK.Infrastructure.Identity;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// RA-3 Reward system (docs/realistic-autonomy.md — PLAN REFORM PRG-5a): the reward
/// memory with the ANTI-REWARD-HACKING STRUCTURAL invariants — the provenance is
/// mandatory (TOLIK never grants itself a reward without a real event source); the
/// store carries NO authority (a reward never touches permissions/identity/authorization
/// — structurally tested by reflection); Effort-vs-Result is honestly typed (the
/// failed work earns the EffortAcknowledgement, never a fake achievement).
/// </summary>
public sealed class RewardStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Provenance_is_mandatory_never_self_awarded()
    {
        var store = new RewardStore();
        Assert.Throws<ArgumentException>(() => store.Record(RewardType.Achievement, "Задача завершена", source: ""));  // no source
        Assert.Throws<ArgumentException>(() => store.Record(RewardType.Achievement, "Задача завершена", source: "  ")); // blank
        Assert.Throws<ArgumentException>(() => store.Record(RewardType.Achievement, "", source: "owner")); // no summary
        store.Record(RewardType.Achievement, "Задача завершена", "system:task_completed", T0);
        Assert.Single(store.Recent(16));
        Assert.Equal("system:task_completed", store.Recent(16)[0].Source); // the real provenance flows
    }

    [Fact]
    public void Rewards_carry_no_authority_structurally()
    {
        // The reflection over the whole RewardStore type: NO method returns or accepts
        // anything authorization-shaped (no principal, no policy, no capability, no
        // decision) — the rewards INFORM, they can never grant.
        var methods = typeof(RewardStore).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(RewardStore))
            .ToList();
        Assert.NotEmpty(methods);
        foreach (var method in methods)
        {
            Assert.NotEqual(typeof(AutonomyDecision), method.ReturnType);
            foreach (var parameter in method.GetParameters())
            {
                Assert.NotEqual(typeof(SessionPrincipal), parameter.ParameterType);
                Assert.NotEqual(typeof(AutonomyDecision), parameter.ParameterType);
            }
        }
    }

    [Fact]
    public void Ring_is_bounded_drop_oldest()
    {
        var store = new RewardStore();
        for (var i = 0; i < RewardStore.Capacity + 50; i++)
            store.Record(RewardType.Social, "Взаимодействие", "event:voice_turn", T0.AddSeconds(i));
        Assert.Equal(RewardStore.Capacity, store.Recent(RewardStore.Capacity).Count);
        Assert.Equal(T0.AddSeconds(50).Second, store.Recent(RewardStore.Capacity)[0].At.Second); // the first 50 dropped
    }

    [Fact]
    public void Effort_vs_result_is_honestly_separate()
    {
        var store = new RewardStore();
        store.Record(RewardType.Achievement, "Сборка зелёная", "system:task_completed", T0);
        store.Record(RewardType.EffortAcknowledgement, "Тест упал, но работа реальная", "system:task_failed", T0.AddMinutes(1));
        var (achievements, efforts) = store.EffortVsResult(TimeSpan.FromHours(1), T0.AddMinutes(2));
        Assert.Equal(1, achievements);
        Assert.Equal(1, efforts);
        // The EffortAcknowledgement is its OWN type — never dressed up as the achievement.
        var entries = store.Recent(16);
        Assert.Equal(RewardType.EffortAcknowledgement, entries[1].Type);
    }
}

/// <summary>
/// ProactivePolicy (docs/mind.md MI-7 — PLAN REFORM PRG-5a): КОГДА ума позволено
/// инициировать контакт. TOLIK NEVER interrupts the user (the quiet window after
/// the interaction); the daily budget is hard-capped; the system signals reach the
/// user; the boredom/curiosity-driven shares are capped hardest; everything else
/// DEFAULT DENY (idle is a valid result — mind §8). The E2 gate is the final gate;
/// this is the mind-side second gate.
/// </summary>
public sealed class ProactivePolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static MindSnapshot Mind(double boredom = 0.2) => new(
        MindState.SnapshotVersion, boredom, 0, 0, 0, 0, 0, 0, "LOW", null, null, T0.AddHours(1));

    [Fact]
    public void Quiet_window_never_interrupts_the_user()
    {
        var decision = ProactivePolicy.Decide(ProactivePolicy.ProactiveKind.SystemSignal,
            Mind(), minutesSinceInteraction: 1.0, proactivesToday: 0, now: T0, locked: false);
        Assert.False(decision.Allowed); // the user just talked — no interruption
        Assert.Contains("quiet", decision.Reason);
    }

    [Fact]
    public void System_signals_reach_after_the_quiet_window()
    {
        var decision = ProactivePolicy.Decide(ProactivePolicy.ProactiveKind.SystemSignal,
            Mind(), minutesSinceInteraction: 10.0, proactivesToday: 0, now: T0, locked: false);
        Assert.True(decision.Allowed); // the honest failure/stall deserves surfacing
    }

    [Fact]
    public void The_daily_budget_is_hard()
    {
        var decision = ProactivePolicy.Decide(ProactivePolicy.ProactiveKind.SystemSignal,
            Mind(), minutesSinceInteraction: 30.0, proactivesToday: 6, now: T0, locked: false, dailyBudget: 6);
        Assert.False(decision.Allowed);
        Assert.Contains("budget", decision.Reason);
    }

    [Fact]
    public void Curiosity_shares_are_capped_harder_and_need_real_boredom()
    {
        // Not bored enough → denied.
        var notBored = ProactivePolicy.Decide(ProactivePolicy.ProactiveKind.CuriosityShare,
            Mind(boredom: 0.2), minutesSinceInteraction: 30.0, proactivesToday: 0, now: T0, locked: false);
        Assert.False(notBored.Allowed);
        // Genuinely bored + the budget has room → allowed.
        var bored = ProactivePolicy.Decide(ProactivePolicy.ProactiveKind.CuriosityShare,
            Mind(boredom: 0.8), minutesSinceInteraction: 30.0, proactivesToday: 0, now: T0, locked: false);
        Assert.True(bored.Allowed);
        // The budget is 2/3 spent → the curiosity share denied (the hardest cap).
        var nearSpent = ProactivePolicy.Decide(ProactivePolicy.ProactiveKind.CuriosityShare,
            Mind(boredom: 0.8), minutesSinceInteraction: 30.0, proactivesToday: 4, now: T0, locked: false, dailyBudget: 6);
        Assert.False(nearSpent.Allowed);
    }

    [Fact]
    public void Everything_else_defaults_to_deny()
    {
        var decision = ProactivePolicy.Decide(ProactivePolicy.ProactiveKind.Other,
            Mind(boredom: 0.9), minutesSinceInteraction: 30.0, proactivesToday: 0, now: T0, locked: false);
        Assert.False(decision.Allowed); // idle is a valid result (mind §8)
    }

    [Fact]
    public void Locked_silences_the_proactivity_entirely()
    {
        var decision = ProactivePolicy.Decide(ProactivePolicy.ProactiveKind.SystemSignal,
            Mind(), minutesSinceInteraction: 30.0, proactivesToday: 0, now: T0, locked: true);
        Assert.False(decision.Allowed);
        Assert.Contains("locked", decision.Reason);
    }
}
