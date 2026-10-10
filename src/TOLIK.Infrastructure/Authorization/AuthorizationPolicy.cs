using System.Text.RegularExpressions;
using TOLIK.Contracts;
using TOLIK.Infrastructure.Identity;

namespace TOLIK.Infrastructure.Authorization;

public enum AuthorizationDecision
{
    Allow,
    Deny,
    ConfirmationRequired
}

/// <summary>Ported from interface/contracts.js ESCALATION_RULES: capability + resource pattern → escalated risk.</summary>
public sealed record EscalationRule(
    string Name,
    AgentCapability Capability,
    Regex Pattern,
    ToolRiskLevel EscalatedRisk,
    bool RequireConfirmation,
    string Reason);

/// <summary>
/// Deterministic risk escalation (AGENTS.md §11, threat T13): certain resources and
/// capabilities carry a higher effective risk than their declared one. Ported from
/// the browser protocol reference (interface/contracts.js), stage 1 of Option A
/// (docs/INTERFACE-ANALYSIS.md).
/// </summary>
public static class ToolEscalation
{
    public static readonly EscalationRule[] Rules =
    [
        new EscalationRule(
            "security-configuration",
            AgentCapability.WriteFile,
            new Regex(@"(\.git[\\/]hooks([\\/].*)?$|nuget\.config$|Directory\.Build\.(props|targets)$|\.tolik([\\/].*)?$|policy\.json$|CODEOWNERS$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            ToolRiskLevel.Destructive,
            RequireConfirmation: true,
            Reason: "modifying build, pipeline or authorization policy configuration"),
        new EscalationRule(
            "security-critical-source",
            AgentCapability.WriteFile,
            new Regex(@"(ProtocolContracts\.cs|CoreRuntime\.cs|AgentRuntime\.cs|AuthorizationEngine\.cs|AuthorizationPolicy\.cs|ToolEscalation\.cs|TolikRoles\.cs|SecurityLogging\.cs|AuditWriter\.cs|DeviceChallengeVerifier\.cs|CapabilityRegistry\.cs|AutonomyPolicy\.cs|AutonomyLevelPolicy\.cs|ConsentModel\.cs|CoreLease\.cs|SignedTransport\.cs|GenesisService\.cs|GenesisVoiceEnrollment\.cs|TrustedPersonEnrollment\.cs|VoiceAdaptationService\.cs|VoiceIdentityDiagnostics\.cs)$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            ToolRiskLevel.Critical,
            RequireConfirmation: true,
            Reason: "modifying TOLIK's own security-critical source can bypass authorization (T13)"),
        new EscalationRule(
            "codex-autonomy",
            AgentCapability.RunCodex,
            new Regex(".", RegexOptions.Compiled),
            ToolRiskLevel.Modify,
            RequireConfirmation: true,
            Reason: "a coding agent can modify the working tree without review of each edit"),
    ];

    /// <summary>Returns the effective risk and the WINNING rule (declared risk is the floor).
    /// Evaluates ALL matching rules and takes the maximum — overlapping rules never
    /// under-escalate (e.g. .tolik hooks + security-critical source both hit → Critical wins).</summary>
    public static (ToolRiskLevel EffectiveRisk, EscalationRule? Applied) Evaluate(AgentCapability capability, string resource)
    {
        var effective = ToolRiskLevel.ReadOnly;
        EscalationRule? winner = null;
        foreach (var rule in Rules)
        {
            if (rule.Capability != capability) continue;
            if (!rule.Pattern.IsMatch(resource)) continue;
            if (rule.EscalatedRisk > effective)
            {
                effective = rule.EscalatedRisk;
                winner = rule;
            }
        }
        return (effective, winner);
    }
}

public sealed record AuthorizationRequest(
    SessionPrincipal? Principal,
    AgentCapability Capability,
    ToolRiskLevel Risk,
    string Resource,
    string AllowedResource,
    bool ConfirmationValid,
    bool PolicyAvailable = true,
    bool SessionValid = true,
    bool DeviceValid = true,
    bool AgentAuthorized = true,
    bool TaskScopeValid = true,
    bool CapabilityGranted = true,
    DateTimeOffset? AuthorizationExpiresAt = null,
    DateTimeOffset? ConfirmationExpiresAt = null,
    bool ConfirmationConsumed = false,
    bool MfaValid = false,
    bool OfflineMode = false,
    bool CoreLocked = false,
    DateTimeOffset? EvaluatedAt = null);

public static class AuthorizationPolicy
{
    public static AuthorizationDecision Evaluate(AuthorizationRequest request)
    {
        var now = request.EvaluatedAt ?? DateTimeOffset.UtcNow;
        var role = TolikRoles.Parse(request.Principal?.Role);
        // LOCKED state (§27, ported from interface/policy.js): the first check in the
        // engine — sensitive operations are denied outright, fail closed.
        if (request.CoreLocked || !request.PolicyAvailable || request.Principal is null || role is null || role is IdentityRole.Guest ||
            !request.SessionValid || !request.DeviceValid ||
            !request.AgentAuthorized || !request.TaskScopeValid || !request.CapabilityGranted ||
            request.Risk == ToolRiskLevel.Critical && !request.MfaValid ||
            string.IsNullOrWhiteSpace(request.Resource) || string.IsNullOrWhiteSpace(request.AllowedResource) ||
            !string.Equals(request.Resource, request.AllowedResource, StringComparison.Ordinal) ||
            request.AuthorizationExpiresAt is { } authorizationExpiry && authorizationExpiry <= now ||
            request.ConfirmationConsumed ||
            request.ConfirmationExpiresAt is { } confirmationExpiry && confirmationExpiry <= now)
            return AuthorizationDecision.Deny;

        // Effective risk = max(declared, escalated): security-critical resources and
        // autonomous coding agents escalate beyond their declared level (T13).
        var effectiveRisk = request.Risk;
        var (escalatedRisk, _) = ToolEscalation.Evaluate(request.Capability, request.Resource);
        if (escalatedRisk > effectiveRisk) effectiveRisk = escalatedRisk;

        // Role ceiling (T15): effective risk may never exceed the principal's ceiling —
        // a hard Deny that confirmation and MFA cannot rescue. This runs after escalation
        // and before any confirmation is issued, so an above-ceiling step can never be
        // parked/approved through an alternate decision path. Fail closed.
        var ceiling = TolikRoles.CeilingFor(role.Value);
        if (ceiling is null || effectiveRisk > ceiling)
            return AuthorizationDecision.Deny;

        // Minimum role per capability (§18/§19, interface/policy.js CAPABILITY_MIN_ROLE):
        // Family/TrustedGuest never gain Codex/OpenCode or generic OS execution regardless
        // of ceilings — a hard Deny that confirmation and MFA cannot rescue. Fail closed.
        if (TolikRoles.RoleOrder(role.Value) < TolikRoles.RoleOrder(TolikRoles.MinimumRoleFor(request.Capability)))
            return AuthorizationDecision.Deny;

        // Offline cap (stage 2c of the interface/ port): in offline mode only the
        // read-only band (ReadOnly/LowRisk — non-modifying per AGENTS.md §11) is
        // authorized; world-modification requires a live lease path. Hard Deny that
        // confirmation cannot rescue. Fail closed.
        if (request.OfflineMode && effectiveRisk > ToolRiskLevel.LowRisk)
            return AuthorizationDecision.Deny;

        return effectiveRisk switch
        {
            ToolRiskLevel.ReadOnly or ToolRiskLevel.LowRisk => AuthorizationDecision.Allow,
            ToolRiskLevel.Modify => request.ConfirmationValid ? AuthorizationDecision.Allow : AuthorizationDecision.ConfirmationRequired,
            ToolRiskLevel.Destructive => request.ConfirmationValid ? AuthorizationDecision.Allow : AuthorizationDecision.ConfirmationRequired,
            ToolRiskLevel.Critical => request.ConfirmationValid && request.MfaValid ? AuthorizationDecision.Allow : AuthorizationDecision.Deny,
            _ => AuthorizationDecision.Deny
        };
    }
}
