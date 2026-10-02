using TOLIK.Contracts;

namespace TOLIK.Infrastructure.Identity;

/// <summary>
/// Typed role model and authorization ceilings (normative: docs/authorization.md;
/// reference: interface/workspace.js ROLE_RISK_CEILING, RISK_CEILING_EXCEEDED).
/// Roles are the single source of truth for who may do what: raw role strings are
/// never trusted — unknown, empty or case-variant names fail closed instead of
/// accidentally matching a privileged role. A ceiling is the maximum EFFECTIVE risk
/// (after escalation, T13) a role can ever be authorized for; it is a hard Deny that
/// confirmation and MFA cannot rescue (T15), so no role can escalate above its band
/// through an alternate decision path.
/// </summary>
public static class TolikRoles
{
    public const string OwnerName = "Owner";
    public const string AuthorizedUserName = "AuthorizedUser";
    public const string TrustedGuestName = "TrustedGuest";
    public const string FamilyName = "Family";

    /// <summary>Strict ordinal parse; returns null for unknown/empty names (fail closed).</summary>
    public static IdentityRole? Parse(string? role) => role switch
    {
        OwnerName => IdentityRole.Owner,
        AuthorizedUserName => IdentityRole.AuthorizedUser,
        TrustedGuestName => IdentityRole.TrustedGuest,
        FamilyName => IdentityRole.Family,
        nameof(IdentityRole.Guest) => IdentityRole.Guest,
        _ => null
    };

    public static bool IsOwner(string? role) => Parse(role) == IdentityRole.Owner;

    /// <summary>Explicit role ordering (normative: interface/policy.js ROLE_ORDER) — never the enum's numeric values.</summary>
    public static int RoleOrder(IdentityRole role) => role switch
    {
        IdentityRole.Owner => 4,
        IdentityRole.AuthorizedUser => 3,
        IdentityRole.Family => 2,
        IdentityRole.TrustedGuest => 1,
        _ => 0
    };

    /// <summary>
    /// Maximum effective risk per role (normative decision matrix + interface/policy.js
    /// ROLE_RISK_CEILING): Owner reaches Critical (still confirmation AND MFA-gated),
    /// AuthorizedUser reaches Destructive (confirmation-gated, never Critical), Family
    /// reaches Modify (§18: no Codex/OpenCode — gated by the minimum-role matrix),
    /// TrustedGuest reaches LowRisk, Guest has no ceiling because it is denied outright
    /// before ceilings are consulted. Null means nothing is allowed.
    /// </summary>
    public static ToolRiskLevel? CeilingFor(IdentityRole role) => role switch
    {
        IdentityRole.Owner => ToolRiskLevel.Critical,
        IdentityRole.AuthorizedUser => ToolRiskLevel.Destructive,
        IdentityRole.Family => ToolRiskLevel.Modify,
        IdentityRole.TrustedGuest => ToolRiskLevel.LowRisk,
        _ => null
    };

    /// <summary>
    /// Minimum role per capability (normative: interface/policy.js CAPABILITY_MIN_ROLE,
    /// §18/§19): Family/TrustedGuest never gain Codex/OpenCode or unrestricted generic
    /// execution regardless of ceilings — a role below the minimum is denied outright.
    /// RunProcess (generic OS execution, not in the browser allowlist) fails closed to
    /// Owner; PlaySound (non-destructive) is Guest-level.
    /// </summary>
    public static IdentityRole MinimumRoleFor(AgentCapability capability) => capability switch
    {
        AgentCapability.ReadFile or AgentCapability.ListDirectory or AgentCapability.GetProcessStatus or AgentCapability.PlaySound => IdentityRole.Guest,
        AgentCapability.RunBuild or AgentCapability.RunTests or AgentCapability.CancelTask => IdentityRole.TrustedGuest,
        AgentCapability.WriteFile => IdentityRole.Family,
        AgentCapability.RunProcess or AgentCapability.RunCodex or AgentCapability.RunOpenCode => IdentityRole.Owner,
        _ => IdentityRole.Owner
    };
}
