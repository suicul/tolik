using TOLIK.Contracts;
using TOLIK.Infrastructure.Authorization;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Phase D/D2 Repair Mode (docs/governance.md §4 — PLAN REFORM PRG-4d): only the
/// REGISTERED recovery procedures exist (the typed registry — never improvised
/// admin commands); the bounded attempts (2 → STOP → explain → ask the owner; the
/// verification resets); InstallTolikUpdate = CRITICAL + MFA + the SIGNED-manifest
/// fail-closed policy (an unsigned blob NEVER installs — T13).
/// </summary>
public sealed class RecoveryRegistryTests
{
    [Fact]
    public void Every_trigger_has_a_registered_procedure_routing_an_existing_capability()
    {
        foreach (var trigger in Enum.GetValues<RecoveryTrigger>())
        {
            var procedure = RecoveryRegistry.Find(trigger);
            Assert.NotNull(procedure); // the registered set covers all the triggers
            Assert.True(procedure.IsValid);
            // The action routes through a REAL registered capability — the authorization
            // pipeline stays mandatory for every recovery action.
            Assert.NotNull(CapabilityRegistry.Find(procedure.Action));
        }
    }

    [Fact]
    public void The_procedures_never_improvise()
    {
        // The fixed names: the known procedures only — there is no API to register
        // an arbitrary one at runtime (the static registry is the whole set).
        var names = RecoveryRegistry.All.Select(p => p.Name).ToList();
        Assert.Contains("restart_voice_service", names);
        Assert.Contains("restore_database_known", names);
        Assert.Equal(5, names.Count); // the fixed five — extension = a code change + a review
    }
}

public sealed class RecoveryAttemptGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Two_attempts_then_stop_explain_ask()
    {
        var gate = new RecoveryAttemptGate();
        var procedure = RecoveryRegistry.Find(RecoveryTrigger.VoiceServiceDown)!;
        Assert.True(gate.TryBegin(procedure, T0));                                  // attempt 1
        Assert.True(gate.TryBegin(procedure, T0.AddMinutes(5)));                      // attempt 2
        Assert.False(gate.TryBegin(procedure, T0.AddMinutes(10)));                     // exhausted → STOP → explain → ask
        Assert.False(gate.TryBegin(procedure, T0.AddMinutes(15)));                     // still exhausted — no infinite retry
    }

    [Fact]
    public void Verification_resets_and_owner_rearms()
    {
        var gate = new RecoveryAttemptGate();
        var procedure = RecoveryRegistry.Find(RecoveryTrigger.ComponentDead)!;
        gate.TryBegin(procedure, T0);
        gate.TryBegin(procedure, T0.AddMinutes(1));
        Assert.False(gate.TryBegin(procedure, T0.AddMinutes(2))); // exhausted
        gate.Rearm(RecoveryTrigger.ComponentDead);                  // the owner's explicit fresh chance
        Assert.True(gate.TryBegin(procedure, T0.AddMinutes(3)));   // one more bounded attempt

        var db = RecoveryRegistry.Find(RecoveryTrigger.DatabaseUnavailable)!;
        gate.TryBegin(db, T0); // MaxAttempts 1 — the most dangerous one is single-shot
        Assert.False(gate.TryBegin(db, T0.AddMinutes(1)));
        gate.MarkResolved(RecoveryTrigger.DatabaseUnavailable);     // the verification passed → reset
        Assert.True(gate.TryBegin(db, T0.AddMinutes(2)));
    }

    [Fact]
    public void Triggers_do_not_share_attempt_budgets()
    {
        var gate = new RecoveryAttemptGate();
        var voice = RecoveryRegistry.Find(RecoveryTrigger.VoiceServiceDown)!;
        var db = RecoveryRegistry.Find(RecoveryTrigger.DatabaseUnavailable)!;
        gate.TryBegin(voice, T0);
        gate.TryBegin(voice, T0.AddSeconds(1));
        Assert.False(gate.TryBegin(voice, T0.AddSeconds(2)));  // the voice budget exhausted
        Assert.True(gate.TryBegin(db, T0.AddSeconds(3)));        // the database budget untouched
    }
}

public sealed class UpdateIntegrityPolicyTests
{
    [Fact]
    public void Only_a_complete_signed_manifest_passes_the_gate()
    {
        var valid = new UpdateManifest("tolik-0.10.3.zip", new string('a', 64), "sig-OWNER-KEY-base64");
        var (allowed, reason) = UpdateIntegrityPolicy.Evaluate(valid);
        Assert.True(allowed);
        Assert.Null(reason);
    }

    [Fact]
    public void Unsigned_or_broken_manifests_never_install()
    {
        Assert.False(UpdateIntegrityPolicy.Evaluate(null).Allowed);                     // no manifest at all
        Assert.False(UpdateIntegrityPolicy.Evaluate(new UpdateManifest("", new string('a', 64), "sig")).Allowed);
        Assert.False(UpdateIntegrityPolicy.Evaluate(new UpdateManifest("tolik.zip", "", "sig")).Allowed);
        Assert.False(UpdateIntegrityPolicy.Evaluate(new UpdateManifest("tolik.zip", new string('a', 64), "")).Allowed);
        Assert.False(UpdateIntegrityPolicy.Evaluate(new UpdateManifest("tolik.zip", "short", "sig")).Allowed);
        Assert.False(UpdateIntegrityPolicy.Evaluate(new UpdateManifest("tolik.zip", new string('Z', 64), "sig")).Allowed); // not hex
        // T13: a tampered/unsigned self-update NEVER installs — the policy is necessary
        // (the signature verification is the installer's mandatory next step, never skipped).
    }

    [Fact]
    public void InstallTolikUpdate_is_critical_mfa_always()
    {
        var descriptor = CapabilityRegistry.Find(AgentCapability.InstallTolikUpdate);
        Assert.NotNull(descriptor);
        Assert.Equal(ToolRiskLevel.Critical, descriptor.DeclaredRisk);
        Assert.True(descriptor.RequiresMfa);              // the self-modification always demands the strongest auth
        Assert.True(descriptor.RequiresConfirmation);
        Assert.False(descriptor.AllowsBackground);        // never a silent background self-update
        Assert.False(descriptor.AllowsSelfInvocation);    // TOLIK never self-installs without the owner
        Assert.True(descriptor.AuditAlways);
    }
}
