using TOLIK.Contracts;
using TOLIK.Infrastructure.Identity;

namespace TOLIK.Infrastructure.Authorization;

/// <summary>
/// Autonomy level (docs/governance.md §2): a layer OVER the existing authorization,
/// not instead of it — every level presupposes the AuthorizationPolicy.Evaluate result
/// and can never relax what it denied. Enforcement of Notify/Confirm/MfaConfirm lands
/// with later phases (E2/F); today the decision is computed and audited only.
/// </summary>
public enum AutonomyDecision
{
    Autonomous,
    Notify,
    Confirm,
    MfaConfirm,
    Deny
}

/// <summary>Deterministic input to the autonomy engine (docs/governance.md §2): who asked, what capability + resource, the existing policy decision, reversibility, channel interactivity, task membership, and an optional owner pre-authorized rule match.</summary>
public sealed record AutonomyContext(
    SessionPrincipal? Principal,
    AgentCapability Capability,
    string Resource,
    AuthorizationDecision PolicyDecision,
    bool Reversible,
    bool Interactive,
    bool PartOfTask,
    bool PreAuthorizedRuleMatches,
    bool OfflineMode = false,
    bool CoreLocked = false);

/// <summary>
/// Deterministic, pure autonomy policy (docs/governance.md §2) — unit-testable without
/// an LLM (AGENTS.md §24). Composes the EXISTING AuthorizationPolicy decision (never a
/// parallel system): LOCKED and a policy Deny outrank everything; pre-authorized rules
/// are explicit owner rules naming capability+target (blanket rules do not exist);
/// unregistered capabilities fail closed (AGENTS.md §14).
/// </summary>
public static class AutonomyPolicy
{
    public static AutonomyDecision Decide(AutonomyContext ctx)
    {
        // LOCKED outranks everything — fail closed (§27).
        if (ctx.CoreLocked) return AutonomyDecision.Deny;
        // Authorization outranks autonomy — a policy Deny is final.
        if (ctx.PolicyDecision == AuthorizationDecision.Deny) return AutonomyDecision.Deny;
        var descriptor = CapabilityRegistry.Find(ctx.Capability);
        // Unregistered capability — fail closed BEFORE any allow path (defense-in-depth:
        // a pre-authorized rule naming an unregistered capability must never yield Autonomous).
        if (descriptor is null) return AutonomyDecision.Deny;
        // Policy already demanded a confirmation: Critical descriptors additionally require MFA (§2, §9).
        if (ctx.PolicyDecision == AuthorizationDecision.ConfirmationRequired)
            return descriptor is { RequiresMfa: true } ? AutonomyDecision.MfaConfirm : AutonomyDecision.Confirm;
        // The owner's explicit structured rule (capability+target, provenance + scope; §2) —
        // never a blanket rule, which the rule format makes impossible.
        if (ctx.PreAuthorizedRuleMatches) return AutonomyDecision.Autonomous;
        var raw = descriptor.DeclaredRisk switch
        {
            ToolRiskLevel.ReadOnly => AutonomyDecision.Autonomous,
            ToolRiskLevel.LowRisk => ctx.Reversible
                ? (ctx.Interactive ? AutonomyDecision.Autonomous : AutonomyDecision.Notify)
                : AutonomyDecision.Notify,
            ToolRiskLevel.Modify or ToolRiskLevel.Destructive => AutonomyDecision.Confirm,
            ToolRiskLevel.Critical => AutonomyDecision.MfaConfirm,
            _ => AutonomyDecision.Deny
        };
        // Autonomy spectrum cap (realistic-autonomy.md RA-0): the configured level
        // demotes decisions (L0→Confirm, L1 Autonomous→Notify); default L2 leaves
        // them unchanged; Deny stays Deny. Identity/level caps NEVER relax a Deny.
        return AutonomyLevelPolicy.Cap(raw);
    }
}
