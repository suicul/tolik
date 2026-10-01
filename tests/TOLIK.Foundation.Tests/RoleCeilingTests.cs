using TOLIK.Contracts;
using TOLIK.Infrastructure.Authorization;
using TOLIK.Infrastructure.Identity;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Roles and authorization ceilings (normative: docs/authorization.md): the ceiling is
/// the maximum EFFECTIVE risk (after escalation, T13) a role can ever reach — a hard
/// Deny no confirmation or MFA can rescue (T15). Unknown/variant role names fail closed.
/// </summary>
public sealed class RoleCeilingTests
{
    private static AuthorizationRequest Request(
        string? role,
        AgentCapability capability,
        ToolRiskLevel risk,
        string resource,
        bool confirmation = false,
        bool mfa = false) => new(
        Principal: new SessionPrincipal(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), role ?? string.Empty),
        Capability: capability,
        Risk: risk,
        Resource: resource,
        AllowedResource: resource,
        ConfirmationValid: confirmation,
        MfaValid: mfa);

    [Theory]
    [InlineData(IdentityRole.Owner, ToolRiskLevel.Critical)]
    [InlineData(IdentityRole.AuthorizedUser, ToolRiskLevel.Destructive)]
    [InlineData(IdentityRole.Guest, null)]
    public void Ceiling_per_role_matches_the_normative_matrix(IdentityRole role, ToolRiskLevel? ceiling)
    {
        Assert.Equal(ceiling, TolikRoles.CeilingFor(role));
    }

    [Fact]
    public void Owner_within_ceiling_still_authorizes_up_to_critical()
    {
        // Owner ceiling is Critical: confirmation + MFA still allows (existing behavior).
        var decision = AuthorizationPolicy.Evaluate(
            Request("Owner", AgentCapability.WriteFile, ToolRiskLevel.Modify, "src/AuthorizationPolicy.cs", confirmation: true, mfa: true));
        Assert.Equal(AuthorizationDecision.Allow, decision);
    }

    [Fact]
    public void AuthorizedUser_with_confirmation_reaches_destructive_ceiling()
    {
        // Existing behavior preserved: AuthorizedUser + confirmation allows the HIGH band.
        var decision = AuthorizationPolicy.Evaluate(
            Request("AuthorizedUser", AgentCapability.WriteFile, ToolRiskLevel.Destructive, "project-a", confirmation: true));
        Assert.Equal(AuthorizationDecision.Allow, decision);
    }

    [Fact]
    public void AuthorizedUser_cannot_escalate_past_ceiling_even_with_mfa_and_confirmation()
    {
        // The escalation-prevention case: a security-critical source escalates WriteFile
        // to Critical — above the AuthorizedUser ceiling. Confirmation + MFA would
        // otherwise allow; the ceiling hard-denies (T15, no privilege escalation).
        var decision = AuthorizationPolicy.Evaluate(
            Request("AuthorizedUser", AgentCapability.WriteFile, ToolRiskLevel.Modify, "src/AuthorizationPolicy.cs", confirmation: true, mfa: true));
        Assert.Equal(AuthorizationDecision.Deny, decision);
    }

    [Fact]
    public void AuthorizedUser_critical_declared_denies_even_with_confirmation_and_mfa()
    {
        var decision = AuthorizationPolicy.Evaluate(
            Request("AuthorizedUser", AgentCapability.WriteFile, ToolRiskLevel.Critical, "project-a", confirmation: true, mfa: true));
        Assert.Equal(AuthorizationDecision.Deny, decision);
    }

    [Fact]
    public void Ceiling_evaluates_effective_escalated_risk_not_declared()
    {
        // LowRisk-declared write onto .git/hooks escalates to Destructive — still within
        // the AuthorizedUser ceiling, so it demands confirmation instead of denying.
        var decision = AuthorizationPolicy.Evaluate(
            Request("AuthorizedUser", AgentCapability.WriteFile, ToolRiskLevel.LowRisk, "repo/.git/hooks/pre-commit"));
        Assert.Equal(AuthorizationDecision.ConfirmationRequired, decision);
    }

    [Fact]
    public void Guest_is_always_denied_at_every_risk_level()
    {
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(Request("Guest", AgentCapability.ReadFile, ToolRiskLevel.ReadOnly, "project-a")));
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(Request("Guest", AgentCapability.RunBuild, ToolRiskLevel.LowRisk, "project-a")));
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(Request("Guest", AgentCapability.WriteFile, ToolRiskLevel.Modify, "project-a", confirmation: true)));
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(Request("Guest", AgentCapability.WriteFile, ToolRiskLevel.Destructive, "project-a", confirmation: true)));
    }

    [Theory]
    [InlineData("Superuser")]
    [InlineData("owner")]
    [InlineData("OWNER")]
    [InlineData(" Owner")]
    [InlineData("Owner ")]
    [InlineData("Admin")]
    [InlineData("")]
    public void Unknown_or_variant_role_names_fail_closed(string? role)
    {
        // Only the exact ordinal "Owner"/"AuthorizedUser" names parse; everything else
        // denies — including case/whitespace variants that would look privileged to a
        // casual string check (fail closed, T15).
        var decision = AuthorizationPolicy.Evaluate(Request(role, AgentCapability.ReadFile, ToolRiskLevel.ReadOnly, "project-a"));
        Assert.Equal(AuthorizationDecision.Deny, decision);
    }

    [Fact]
    public void Parse_is_strict_ordinal_and_typed()
    {
        Assert.Equal(IdentityRole.Owner, TolikRoles.Parse("Owner"));
        Assert.Equal(IdentityRole.AuthorizedUser, TolikRoles.Parse("AuthorizedUser"));
        Assert.Equal(IdentityRole.Guest, TolikRoles.Parse("Guest"));
        Assert.Null(TolikRoles.Parse("owner"));
        Assert.Null(TolikRoles.Parse("Owner "));
        Assert.Null(TolikRoles.Parse("Superuser"));
        Assert.Null(TolikRoles.Parse(""));
        Assert.Null(TolikRoles.Parse(null));
        Assert.True(TolikRoles.IsOwner("Owner"));
        Assert.False(TolikRoles.IsOwner("AuthorizedUser"));
        Assert.False(TolikRoles.IsOwner("owner"));
        Assert.False(TolikRoles.IsOwner(null));
    }
}
