using TOLIK.Contracts;
using TOLIK.Infrastructure.Authorization;
using TOLIK.Infrastructure.Identity;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Family/TrustedGuest roles (docs/SECURITY.md §18, interface/policy.js matrices):
/// explicit parse, risk ceilings (TrustedGuest→LowRisk, Family→Modify) and the
/// capability minimum-role matrix — Family/TrustedGuest never gain Codex/OpenCode or
/// generic OS execution regardless of ceilings (hard Deny, no rescue). The owner's
/// private memory and audit stay owner/user-scoped (existing enforcement, verified).
/// </summary>
public sealed class FamilyRoleTests
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
    [InlineData("Family", IdentityRole.Family)]
    [InlineData("TrustedGuest", IdentityRole.TrustedGuest)]
    public void New_roles_parse_strictly(string name, IdentityRole expected)
    {
        Assert.Equal(expected, TolikRoles.Parse(name));
        Assert.Null(TolikRoles.Parse("family")); // case-variant fails closed
        Assert.Null(TolikRoles.Parse("trustedguest"));
    }

    [Theory]
    [InlineData(IdentityRole.TrustedGuest, ToolRiskLevel.LowRisk)]
    [InlineData(IdentityRole.Family, ToolRiskLevel.Modify)]
    public void Ceilings_match_the_normative_matrix(IdentityRole role, ToolRiskLevel ceiling)
    {
        Assert.Equal(ceiling, TolikRoles.CeilingFor(role));
    }

    [Fact]
    public void Family_cannot_run_codex_or_opencode_or_run_process()
    {
        // §18: Family must NOT gain Codex/OpenCode; generic OS execution fails closed.
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(
            Request("Family", AgentCapability.RunCodex, ToolRiskLevel.Modify, "any", confirmation: true, mfa: true)));
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(
            Request("Family", AgentCapability.RunOpenCode, ToolRiskLevel.Modify, "any", confirmation: true, mfa: true)));
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(
            Request("Family", AgentCapability.RunProcess, ToolRiskLevel.LowRisk, "any", confirmation: true)));
    }

    [Fact]
    public void Trusted_guest_cannot_write_or_run_codex()
    {
        // TrustedGuest ceiling is LowRisk and the WriteFile minimum role is Family:
        // writes and coding agents are denied outright.
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(
            Request("TrustedGuest", AgentCapability.WriteFile, ToolRiskLevel.Modify, "project-a", confirmation: true)));
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(
            Request("TrustedGuest", AgentCapability.RunCodex, ToolRiskLevel.Modify, "any", confirmation: true)));
    }

    [Fact]
    public void Trusted_guest_runs_builds_and_tests_within_its_band()
    {
        Assert.Equal(AuthorizationDecision.Allow, AuthorizationPolicy.Evaluate(
            Request("TrustedGuest", AgentCapability.RunBuild, ToolRiskLevel.LowRisk, "project-a")));
        Assert.Equal(AuthorizationDecision.Allow, AuthorizationPolicy.Evaluate(
            Request("TrustedGuest", AgentCapability.RunTests, ToolRiskLevel.LowRisk, "project-a")));
        Assert.Equal(AuthorizationDecision.Allow, AuthorizationPolicy.Evaluate(
            Request("TrustedGuest", AgentCapability.ReadFile, ToolRiskLevel.ReadOnly, "project-a")));
    }

    [Fact]
    public void Family_writes_within_ceiling_and_reads_readonly()
    {
        Assert.Equal(AuthorizationDecision.Allow, AuthorizationPolicy.Evaluate(
            Request("Family", AgentCapability.WriteFile, ToolRiskLevel.Modify, "project-a", confirmation: true)));
        Assert.Equal(AuthorizationDecision.Allow, AuthorizationPolicy.Evaluate(
            Request("Family", AgentCapability.ReadFile, ToolRiskLevel.ReadOnly, "project-a")));
    }

    [Fact]
    public void Owner_is_unaffected_by_the_minimum_role_matrix()
    {
        Assert.Equal(AuthorizationDecision.Allow, AuthorizationPolicy.Evaluate(
            Request("Owner", AgentCapability.RunCodex, ToolRiskLevel.Modify, "any", confirmation: true)));
        Assert.Equal(AuthorizationDecision.Allow, AuthorizationPolicy.Evaluate(
            Request("Owner", AgentCapability.RunProcess, ToolRiskLevel.LowRisk, "any")));
    }

    [Fact]
    public void Escalated_write_still_denied_for_family_above_ceiling()
    {
        // LowRisk-declared write onto .git/hooks escalates to Destructive — above the
        // Family ceiling (Modify) → hard Deny (T15 interplay with §18).
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(
            Request("Family", AgentCapability.WriteFile, ToolRiskLevel.LowRisk, "repo/.git/hooks/pre-commit", confirmation: true)));
    }
}
