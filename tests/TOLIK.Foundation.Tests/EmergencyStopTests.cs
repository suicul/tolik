using TOLIK.Contracts;
using TOLIK.Core;
using TOLIK.Infrastructure.Authorization;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Emergency Stop (docs/governance.md §8, Phase G — PLAN REFORM PRG-4a): the owner's
/// SOFT kill switch — a managed autonomy pause. The NEW PRIVILEGED operations DENY
/// (Modify/Destructive/Critical — including the pre-authorized rules); the READS
/// stay live (the owner keeps the channel to chat/ask/resume). The mind rests
/// (Idle-only occupations). NEVER touches identity/memory/Genesis/sessions —
/// that is LOCKED's territory (the security-incident state), and the two never mix.
/// </summary>
public sealed class EmergencyStopTests
{
    private static AutonomyContext Ctx(AgentCapability capability, AuthorizationDecision policy = AuthorizationDecision.Allow, bool emergency = false, bool preAuth = false) =>
        new(null, capability, "resource", policy, Reversible: true, Interactive: true, PartOfTask: false, preAuth, EmergencyStopped: emergency);

    [Fact]
    public void Stopped_denies_privileged_operations()
    {
        // A benign WriteFile (Modify) with the policy Allow — stopped → DENY.
        Assert.Equal(AutonomyDecision.Deny, AutonomyPolicy.Decide(Ctx(AgentCapability.WriteFile, emergency: true)));
        // A destructive-capable run — stopped → DENY.
        Assert.Equal(AutonomyDecision.Deny, AutonomyPolicy.Decide(Ctx(AgentCapability.RunCodex, emergency: true)));
    }

    [Fact]
    public void Stopped_keeps_reads_alive_the_owner_keeps_the_channel()
    {
        Assert.Equal(AutonomyDecision.Autonomous, AutonomyPolicy.Decide(Ctx(AgentCapability.ReadFile, emergency: true)));
        Assert.Equal(AutonomyDecision.Autonomous, AutonomyPolicy.Decide(Ctx(AgentCapability.ListDirectory, emergency: true)));
    }

    [Fact]
    public void Stopped_denies_even_pre_authorized_privileged_rules()
    {
        // The owner's explicit pre-authorized rule for WriteFile — the emergency stop
        // still halts it: the pause outranks the pre-authorization (restriction only).
        Assert.Equal(AutonomyDecision.Deny,
            AutonomyPolicy.Decide(Ctx(AgentCapability.WriteFile, emergency: true, preAuth: true)));
    }

    [Fact]
    public void Stopped_still_fails_closed_on_unregistered_and_locked()
    {
        Assert.Equal(AutonomyDecision.Deny, AutonomyPolicy.Decide(Ctx((AgentCapability)999, emergency: true)));
        var lockedAndStopped = Ctx(AgentCapability.ReadFile, emergency: true) with { CoreLocked = true };
        Assert.Equal(AutonomyDecision.Deny, AutonomyPolicy.Decide(lockedAndStopped)); // LOCKED outranks everything
    }

    [Fact]
    public void The_state_is_a_simple_owner_toggle()
    {
        var state = new EmergencyStopState();
        Assert.False(state.IsStopped);
        state.Stop();
        Assert.True(state.IsStopped);   // always safe to stop
        state.Resume();
        Assert.False(state.IsStopped);  // the deliberate resume
    }

    [Fact]
    public void Stopped_mind_rests_idle_only_occupations()
    {
        var mind = new MindRuntime();
        mind.MarkEmergencyStopped(true);
        for (var i = 0; i < 40; i++) mind.Tick(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero).AddSeconds(i));
        var entries = mind.Journal.Recent(32);
        var occupations = entries.Where(e => e.Kind == MindJournalKind.ActivitySelected).Select(e => e.Summary).ToList();
        // While stopped, every selected occupation is Покой — the mind rests, never poses.
        Assert.All(occupations, o => Assert.Equal(ActivityCandidate.Idle.Summary, o));

        // The resume re-enables the occupations.
        mind.MarkEmergencyStopped(false);
        mind.OnTaskFailed(); // the curiosity/frustration spike → a non-idle occupation becomes likely
        for (var i = 40; i < 80; i++) mind.Tick(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero).AddSeconds(i));
        Assert.True(mind.Snapshot().Curiosity > 0 || mind.Snapshot().Frustration > 0); // the mind lives on
    }

    [Fact]
    public void Selector_idleOnly_beats_even_the_strongest_drives()
    {
        var selector = new ActivitySelector();
        var strong = new[]
        {
            new DriveState(DriveNames.Curiosity, 1.0), new DriveState(DriveNames.Novelty, 1.0),
            new DriveState(DriveNames.Exploration, 1.0), new DriveState(DriveNames.Maintenance, 1.0)
        };
        var selection = selector.Select(strong, new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero), locked: false, idleOnly: true);
        Assert.Equal(ActivityNames.Idle, selection.Activity); // the strongest drives cannot break the rest
    }
}
