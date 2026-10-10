using TOLIK.Core;
using TOLIK.Infrastructure.Authorization;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Realistic autonomy pre-Genesis (docs/realistic-autonomy.md RA-0..2): the autonomy
/// spectrum L0-L5 (a spectrum capping decisions, never a boolean; Deny stays Deny),
/// the structured revocable ConsentModel (target-bound, never "UserAllowed = true"),
/// and the multi-dimensional TrustModel (trust ≠ authorization, never self-escalating,
/// evidence over obedience).
/// </summary>
public sealed class AutonomyLevelTests
{
    [Fact]
    public void Default_level_is_supervised_autonomous()
    {
        // TOLIK_AUTONOMY_LEVEL unset in test env → default L2 (docs §1).
        Assert.Equal(AutonomyLevel.SupervisedAutonomous, AutonomyLevelPolicy.Configured);
    }

    [Fact]
    public void Reactive_level_demotes_autonomous_to_confirm()
    {
        // L0: nothing acts without asking — even "safe" autonomous becomes Confirm.
        Assert.Equal(AutonomyDecision.Confirm,
            AutonomyLevelPolicy.Cap(AutonomyDecision.Autonomous, AutonomyLevel.Reactive));
        Assert.Equal(AutonomyDecision.Confirm,
            AutonomyLevelPolicy.Cap(AutonomyDecision.Notify, AutonomyLevel.Reactive));
    }

    [Fact]
    public void Assisted_level_demotes_autonomous_to_notify()
    {
        // L1: acts, but reports — never silent.
        Assert.Equal(AutonomyDecision.Notify,
            AutonomyLevelPolicy.Cap(AutonomyDecision.Autonomous, AutonomyLevel.Assisted));
        Assert.Equal(AutonomyDecision.Confirm,
            AutonomyLevelPolicy.Cap(AutonomyDecision.Confirm, AutonomyLevel.Assisted));
    }

    [Fact]
    public void Deny_stays_deny_at_every_level()
    {
        // Authorization outranks the level: a denial is never relaxed.
        foreach (var level in Enum.GetValues<AutonomyLevel>())
            Assert.Equal(AutonomyDecision.Deny, AutonomyLevelPolicy.Cap(AutonomyDecision.Deny, level));
    }

    [Fact]
    public void Supervised_and_above_leave_decisions_untouched()
    {
        Assert.Equal(AutonomyDecision.Autonomous,
            AutonomyLevelPolicy.Cap(AutonomyDecision.Autonomous, AutonomyLevel.SupervisedAutonomous));
        Assert.Equal(AutonomyDecision.MfaConfirm,
            AutonomyLevelPolicy.Cap(AutonomyDecision.MfaConfirm, AutonomyLevel.LongTermExperimental));
    }
}

public sealed class ConsentModelTests
{
    [Fact]
    public void Grant_is_target_bound()
    {
        ConsentModel.Grant("fix_service", "voice-service", ConsentScopeKind.TaskScoped);
        Assert.NotNull(ConsentModel.FindActive("fix_service", "voice-service"));
        // «почини голосовой сервис» ≠ «modify anything» (§4/§15).
        Assert.Null(ConsentModel.FindActive("fix_service", "database"));
        ConsentModel.RevokeAll("fix_service");
    }

    [Fact]
    public void Expired_grant_is_not_active()
    {
        ConsentModel.Grant("test_action", "target-a", ConsentScopeKind.OneTime, duration: TimeSpan.FromMinutes(5));
        var now = DateTimeOffset.UtcNow;
        Assert.NotNull(ConsentModel.FindActive("test_action", "target-a", now));
        Assert.Null(ConsentModel.FindActive("test_action", "target-a", now + TimeSpan.FromMinutes(6)));
        ConsentModel.RevokeAll("test_action");
    }

    [Fact]
    public void Revoke_all_stops_every_grant_for_action()
    {
        ConsentModel.Grant("cleanup", "t1", ConsentScopeKind.SessionScoped);
        ConsentModel.Grant("cleanup", "t2", ConsentScopeKind.SessionScoped);
        Assert.True(ConsentModel.RevokeAll("cleanup"));
        Assert.Null(ConsentModel.FindActive("cleanup", "t1"));
        Assert.Null(ConsentModel.FindActive("cleanup", "t2"));
        Assert.False(ConsentModel.RevokeAll("cleanup")); // nothing left
    }

    [Fact]
    public void Bounded_phrases_parse_to_scoped_grants()
    {
        Assert.Equal(ConsentScopeKind.TaskScoped, ConsentModel.ParseGrantPhrase("Делай пока не закончишь").Scope);
        var absence = ConsentModel.ParseGrantPhrase("Пока меня нет — занимайся этим");
        Assert.Equal(ConsentScopeKind.TaskScoped, absence.Scope);
        Assert.Equal(TimeSpan.FromHours(8), absence.Duration); // the absence lease: bounded 8h
        Assert.Equal(ConsentScopeKind.SessionScoped, ConsentModel.ParseGrantPhrase("Управляй в этой сессии").Scope);
        // "всегда/постоянно" → PersistentLowRisk only — NEVER high-risk (§4/§13).
        Assert.Equal(ConsentScopeKind.PersistentLowRisk, ConsentModel.ParseGrantPhrase("Можешь постоянно поддерживать").Scope);
    }

    [Fact]
    public void Ambiguous_phrase_falls_back_to_one_time()
    {
        // Never a broad permission from an ambiguous phrase (§14).
        Assert.Equal(ConsentScopeKind.OneTime, ConsentModel.ParseGrantPhrase("ну, давай как-нибудь").Scope);
        Assert.Equal(ConsentScopeKind.OneTime, ConsentModel.ParseGrantPhrase(null).Scope);
        Assert.Equal(ConsentScopeKind.OneTime, ConsentModel.ParseGrantPhrase("   ").Scope);
    }
}

public sealed class TrustProfileTests
{
    [Fact]
    public void Starts_at_baseline_and_is_bounded()
    {
        var trust = new TrustProfile();
        Assert.Equal(TrustProfile.Baseline, trust.Get("technical"), 6);
        for (var i = 0; i < 100; i++)
            trust.Apply(TrustEvidenceKind.SuccessfulSkill, "technical", 1.0, "task ok");
        Assert.InRange(trust.Get("technical"), 0.0, TrustProfile.MaxTrust); // never "absolute" (§3.6)
    }

    [Fact]
    public void Negative_evidence_outweighs_positive()
    {
        var up = new TrustProfile();
        up.Apply(TrustEvidenceKind.SuccessfulTask, "reliability", 1.0, "task ok");
        var down = new TrustProfile();
        down.Apply(TrustEvidenceKind.FalseReport, "reliability", 1.0, "claimed done, was not");
        // One false report hurts more than one success helps (honesty dominates).
        Assert.True(TrustProfile.Baseline - down.Get("reliability") > up.Get("reliability") - TrustProfile.Baseline);
    }

    [Fact]
    public void Good_judgment_is_evidence_not_obedience()
    {
        // «Уточни, что именно» on an ambiguous destructive command = GOOD judgment (§3.5).
        var trust = new TrustProfile();
        trust.Apply(TrustEvidenceKind.GoodJudgment, "safety", 1.0, "asked for clarification instead of deleting");
        Assert.True(trust.Get("safety") > TrustProfile.Baseline);
    }

    [Fact]
    public void Dimensions_are_independent()
    {
        var trust = new TrustProfile();
        for (var i = 0; i < 50; i++)
            trust.Apply(TrustEvidenceKind.SuccessfulSkill, "technical", 1.0, "skill ok");
        Assert.True(trust.Get("technical") > TrustProfile.Baseline);
        Assert.Equal(TrustProfile.Baseline, trust.Get("social"), 6); // untouched
    }

    [Fact]
    public void Decay_pulls_toward_baseline()
    {
        var trust = new TrustProfile();
        for (var i = 0; i < 50; i++)
            trust.Apply(TrustEvidenceKind.SuccessfulSkill, "technical", 1.0, "skill ok");
        var high = trust.Get("technical");
        trust.Decay(7.0 * 24 * 60); // 7 days → ~50% of the way back
        var after = trust.Get("technical");
        Assert.True(after < high);
        Assert.True(after > TrustProfile.Baseline); // partial, not a reset
        Assert.InRange(after, 0.0, TrustProfile.MaxTrust);
    }

    [Fact]
    public void Provenance_is_recorded_and_bounded()
    {
        var trust = new TrustProfile();
        for (var i = 0; i < 30; i++)
            trust.Apply(TrustEvidenceKind.HonestReport, "communication", 0.9, $"report {i}");
        var evidence = trust.Evidence();
        Assert.Equal(16, evidence.Count); // bounded provenance ring
        Assert.All(evidence, e =>
        {
            Assert.NotEqual(default, e.At);
            Assert.False(string.IsNullOrWhiteSpace(e.Reason));
            Assert.InRange(e.Confidence, 0.0, 1.0);
        });
    }

    [Fact]
    public void Unknown_dimension_fails_closed()
    {
        var trust = new TrustProfile();
        Assert.Throws<ArgumentException>(() => trust.Get("charisma"));
        Assert.Throws<ArgumentException>(() => trust.Apply(TrustEvidenceKind.SuccessfulTask, "charisma", 1.0, "x"));
        Assert.Throws<ArgumentException>(() => trust.Apply(TrustEvidenceKind.SuccessfulTask, "safety", 1.0, "")); // no reason → no silent drift
        Assert.Throws<ArgumentOutOfRangeException>(() => trust.Apply(TrustEvidenceKind.SuccessfulTask, "safety", double.NaN, "x"));
    }
}
