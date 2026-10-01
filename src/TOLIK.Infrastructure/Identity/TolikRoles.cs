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

    /// <summary>Strict ordinal parse; returns null for unknown/empty names (fail closed).</summary>
    public static IdentityRole? Parse(string? role) => role switch
    {
        OwnerName => IdentityRole.Owner,
        AuthorizedUserName => IdentityRole.AuthorizedUser,
        nameof(IdentityRole.Guest) => IdentityRole.Guest,
        _ => null
    };

    public static bool IsOwner(string? role) => Parse(role) == IdentityRole.Owner;

    /// <summary>
    /// Maximum effective risk per role (normative decision matrix): Owner reaches
    /// Critical (still confirmation AND MFA-gated), AuthorizedUser reaches Destructive
    /// (confirmation-gated, never Critical), Guest has no ceiling because it is denied
    /// outright before ceilings are consulted. Null means nothing is allowed.
    /// </summary>
    public static ToolRiskLevel? CeilingFor(IdentityRole role) => role switch
    {
        IdentityRole.Owner => ToolRiskLevel.Critical,
        IdentityRole.AuthorizedUser => ToolRiskLevel.Destructive,
        _ => null
    };
}
