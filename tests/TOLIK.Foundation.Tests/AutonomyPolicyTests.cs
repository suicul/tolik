using TOLIK.Contracts;
using TOLIK.Infrastructure.Authorization;
using TOLIK.Infrastructure.Identity;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Deterministic autonomy policy (docs/governance.md §2): a layer OVER the existing
/// authorization, not instead of it. Fail closed everywhere (LOCKED / policy Deny /
/// unknown capability → Deny).
/// </summary>
public sealed class AutonomyPolicyTests
{
    private static AutonomyContext Context(
        AgentCapability capability,
        AuthorizationDecision policyDecision,
        bool reversible = true,
        bool interactive = true,
        bool preAuthorized = false,
        bool coreLocked = false) => new(
        Principal: new SessionPrincipal(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Owner"),
        Capability: capability,
        Resource: "C:/tolik/test-target",
        PolicyDecision: policyDecision,
        Reversible: reversible,
        Interactive: interactive,
        PartOfTask: true,
        PreAuthorizedRuleMatches: preAuthorized,
        CoreLocked: coreLocked);

    [Fact]
    public void CoreLocked_denies_even_when_policy_allowed()
    {
        // LOCKED outranks everything — fail closed (§27).
        Assert.Equal(AutonomyDecision.Deny,
            AutonomyPolicy.Decide(Context(AgentCapability.ReadFile, AuthorizationDecision.Allow, coreLocked: true)));
    }

    [Fact]
    public void PolicyDeny_outranks_autonomy()
    {
        // Authorization outranks autonomy (§2): a policy Deny is final.
        Assert.Equal(AutonomyDecision.Deny,
            AutonomyPolicy.Decide(Context(AgentCapability.ReadFile, AuthorizationDecision.Deny)));
    }

    [Fact]
    public void ConfirmationRequired_with_RunCodex_requires_Confirm()
    {
        // RunCodex: RequiresConfirmation=true, RequiresMfa=false (CapabilityRegistry).
        // The MfaConfirm branch under ConfirmationRequired is defensive: no registered
        // capability declares RequiresMfa=true and the static registry cannot be faked —
        // it activates only if a future descriptor gains RequiresMfa (governance §2).
        Assert.Equal(AutonomyDecision.Confirm,
            AutonomyPolicy.Decide(Context(AgentCapability.RunCodex, AuthorizationDecision.ConfirmationRequired)));
    }

    [Fact]
    public void PreAuthorizedRule_allow_low_risk_is_Autonomous()
    {
        // The owner's explicit structured rule (capability+target, provenance; §2) —
        // blanket rules never exist because a rule must name capability+target.
        Assert.Equal(AutonomyDecision.Autonomous,
            AutonomyPolicy.Decide(Context(AgentCapability.RunBuild, AuthorizationDecision.Allow, preAuthorized: true)));
    }

    [Fact]
    public void PreAuthorizedRule_outranks_notify_band()
    {
        // A named owner rule also lifts a non-reversible LowRisk action above NOTIFY (§2).
        Assert.Equal(AutonomyDecision.Autonomous,
            AutonomyPolicy.Decide(Context(AgentCapability.PlaySound, AuthorizationDecision.Allow, reversible: false, preAuthorized: true)));
    }

    [Fact]
    public void ReadOnly_allow_is_Autonomous()
    {
        Assert.Equal(AutonomyDecision.Autonomous,
            AutonomyPolicy.Decide(Context(AgentCapability.ReadFile, AuthorizationDecision.Allow)));
    }

    [Fact]
    public void LowRisk_reversible_interactive_is_Autonomous()
    {
        Assert.Equal(AutonomyDecision.Autonomous,
            AutonomyPolicy.Decide(Context(AgentCapability.RunBuild, AuthorizationDecision.Allow, reversible: true, interactive: true)));
    }

    [Fact]
    public void LowRisk_reversible_not_interactive_is_Notify()
    {
        // The same action at night/away: do it, report instead of silence (§2 context-aware).
        Assert.Equal(AutonomyDecision.Notify,
            AutonomyPolicy.Decide(Context(AgentCapability.RunBuild, AuthorizationDecision.Allow, reversible: true, interactive: false)));
    }

    [Fact]
    public void LowRisk_not_reversible_is_Notify()
    {
        Assert.Equal(AutonomyDecision.Notify,
            AutonomyPolicy.Decide(Context(AgentCapability.PlaySound, AuthorizationDecision.Allow, reversible: false)));
    }

    [Fact]
    public void Unknown_capability_allow_denies()
    {
        // Unregistered capability — fail closed (AGENTS.md §14).
        Assert.Equal(AutonomyDecision.Deny,
            AutonomyPolicy.Decide(Context((AgentCapability)999, AuthorizationDecision.Allow)));
    }

    [Fact]
    public void Modify_allow_confirms_defensively()
    {
        // Normally unreachable: AuthorizationPolicy already demands confirmation upstream.
        Assert.Equal(AutonomyDecision.Confirm,
            AutonomyPolicy.Decide(Context(AgentCapability.WriteFile, AuthorizationDecision.Allow)));
    }
}
