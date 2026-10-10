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
        // Pinned (not machine-dependent): TOLIK_AUTONOMY_LEVEL unset → default L2 (docs §1).
        var old = Environment.GetEnvironmentVariable("TOLIK_AUTONOMY_LEVEL");
        try
        {
            Environment.SetEnvironmentVariable("TOLIK_AUTONOMY_LEVEL", null);
            Assert.Equal(AutonomyLevel.SupervisedAutonomous, AutonomyLevelPolicy.Configured);
        }
        finally { Environment.SetEnvironmentVariable("TOLIK_AUTONOMY_LEVEL", old); }
    }

    [Fact]
    public void Invalid_config_fails_closed_to_reactive()
    {
        // M1 (audit): Enum.TryParse accepts out-of-range numerics ("99") — an invalid
        // value must fall to the MOST RESTRICTIVE level, never the permissive default.
        var old = Environment.GetEnvironmentVariable("TOLIK_AUTONOMY_LEVEL");
        try
        {
            Environment.SetEnvironmentVariable("TOLIK_AUTONOMY_LEVEL", "99");
            Assert.Equal(AutonomyLevel.Reactive, AutonomyLevelPolicy.Configured);
            Environment.SetEnvironmentVariable("TOLIK_AUTONOMY_LEVEL", "nonsense");
            Assert.Equal(AutonomyLevel.Reactive, AutonomyLevelPolicy.Configured);
        }
        finally { Environment.SetEnvironmentVariable("TOLIK_AUTONOMY_LEVEL", old); }
    }

    [Fact]
    public void Valid_config_parses_by_name()
    {
        var old = Environment.GetEnvironmentVariable("TOLIK_AUTONOMY_LEVEL");
        try
        {
            Environment.SetEnvironmentVariable("TOLIK_AUTONOMY_LEVEL", "BoundedAutonomous");
            Assert.Equal(AutonomyLevel.BoundedAutonomous, AutonomyLevelPolicy.Configured);
        }
        finally { Environment.SetEnvironmentVariable("TOLIK_AUTONOMY_LEVEL", old); }
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
    private const string Ivan = "owner-ivan";
    private const string Maria = "family-maria";

    [Fact]
    public void Grant_is_target_bound()
    {
        ConsentModel.Grant("fix_service", "voice-service", ConsentScopeKind.TaskScoped, Ivan);
        Assert.NotNull(ConsentModel.FindActive("fix_service", "voice-service", Ivan));
        // «почини голосовой сервис» ≠ «modify anything» (§4/§15).
        Assert.Null(ConsentModel.FindActive("fix_service", "database", Ivan));
        ConsentModel.RevokeAll("fix_service");
    }

    [Fact]
    public void Grant_is_identity_bound()
    {
        // M2 (audit): consent is identity-bound — a household member never consumes
        // another's grant (multi-person memory, §18/§19).
        ConsentModel.Grant("fix_service", "voice-service", ConsentScopeKind.TaskScoped, Ivan);
        Assert.NotNull(ConsentModel.FindActive("fix_service", "voice-service", Ivan));
        Assert.Null(ConsentModel.FindActive("fix_service", "voice-service", Maria));
        ConsentModel.RevokeAll("fix_service");
    }

    [Fact]
    public void GrantedBy_is_required()
    {
        // M2 (audit): a grant without a principal fails closed — no anonymous consent.
        Assert.Throws<ArgumentException>(() =>
            ConsentModel.Grant("fix_service", "voice-service", ConsentScopeKind.TaskScoped, ""));
        Assert.Throws<ArgumentException>(() =>
            ConsentModel.FindActive("fix_service", "voice-service", ""));
    }

    [Fact]
    public void Expired_grant_is_not_active_and_is_swept()
    {
        ConsentModel.Grant("test_action", "target-a", ConsentScopeKind.OneTime, Ivan, duration: TimeSpan.FromMinutes(5));
        var now = DateTimeOffset.UtcNow;
        Assert.NotNull(ConsentModel.FindActive("test_action", "target-a", Ivan, now));
        Assert.Null(ConsentModel.FindActive("test_action", "target-a", Ivan, now + TimeSpan.FromMinutes(6)));
        ConsentModel.RevokeAll("test_action");
    }

    [Fact]
    public void OneTime_grant_is_consumed_after_use()
    {
        // M3 (audit): "OneTime — consumed after use" is enforced, not just a comment.
        ConsentModel.Grant("screenshot_once", "desktop", ConsentScopeKind.OneTime, Ivan);
        Assert.NotNull(ConsentModel.FindActive("screenshot_once", "desktop", Ivan));
        Assert.True(ConsentModel.Consume("screenshot_once", "desktop", Ivan));
        Assert.Null(ConsentModel.FindActive("screenshot_once", "desktop", Ivan));
        Assert.False(ConsentModel.Consume("screenshot_once", "desktop", Ivan)); // nothing to consume
    }

    [Fact]
    public void TaskScoped_grant_is_not_consumable()
    {
        // Consume() is for OneTime only — task/session grants live until revoked/bounded.
        ConsentModel.Grant("maintain_service", "voice-service", ConsentScopeKind.TaskScoped, Ivan);
        Assert.False(ConsentModel.Consume("maintain_service", "voice-service", Ivan));
        Assert.NotNull(ConsentModel.FindActive("maintain_service", "voice-service", Ivan));
        ConsentModel.RevokeAll("maintain_service");
    }

    [Fact]
    public void Session_scoped_requires_bound_session_and_revokes_on_session_end()
    {
        // M3 (audit): "until the session ends" must be enforceable — a SessionScoped
        // grant without a session id fails closed; the session-end flow revokes it.
        Assert.Throws<ArgumentException>(() =>
            ConsentModel.Grant("manage_app", "app", ConsentScopeKind.SessionScoped, Ivan));
        ConsentModel.Grant("manage_app", "app", ConsentScopeKind.SessionScoped, Ivan, boundSessionId: "ses_123");
        Assert.NotNull(ConsentModel.FindActive("manage_app", "app", Ivan));
        Assert.True(ConsentModel.RevokeSession("ses_123"));
        Assert.Null(ConsentModel.FindActive("manage_app", "app", Ivan));
    }

    [Fact]
    public void Active_grant_list_is_bounded()
    {
        // M3 (audit): the list is capped (drop-oldest) — long-lived cores never grow unbounded.
        for (var i = 0; i < 300; i++)
            ConsentModel.Grant("bulk_action", $"t{i}", ConsentScopeKind.OneTime, Ivan);
        // The oldest 44 dropped; the newest 256 remain active.
        Assert.Null(ConsentModel.FindActive("bulk_action", "t0", Ivan));
        Assert.NotNull(ConsentModel.FindActive("bulk_action", "t299", Ivan));
        ConsentModel.RevokeAll("bulk_action");
    }

    [Fact]
    public void Revoke_all_stops_every_grant_for_action()
    {
        ConsentModel.Grant("cleanup", "t1", ConsentScopeKind.SessionScoped, Ivan, boundSessionId: "ses_x1");
        ConsentModel.Grant("cleanup", "t2", ConsentScopeKind.SessionScoped, Ivan, boundSessionId: "ses_x2");
        Assert.True(ConsentModel.RevokeAll("cleanup"));
        Assert.Null(ConsentModel.FindActive("cleanup", "t1", Ivan));
        Assert.Null(ConsentModel.FindActive("cleanup", "t2", Ivan));
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
