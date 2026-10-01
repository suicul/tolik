using TOLIK.Contracts;
using TOLIK.Infrastructure.Authorization;
using TOLIK.Infrastructure.Identity;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Escalation rules ported from the browser protocol reference (interface/contracts.js):
/// security-critical resources escalate beyond their declared risk (AGENTS.md §11, T13).
/// </summary>
public sealed class ToolEscalationTests
{
    private static AuthorizationRequest Request(
        AgentCapability capability,
        ToolRiskLevel declared,
        string resource,
        bool confirmation = false,
        bool mfa = false) => new(
        Principal: new SessionPrincipal(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Owner"),
        Capability: capability,
        Risk: declared,
        Resource: resource,
        AllowedResource: resource,
        ConfirmationValid: confirmation,
        MfaValid: mfa);

    [Fact]
    public void WriteFile_to_git_hooks_escalates_to_Destructive()
    {
        var (risk, rule) = ToolEscalation.Evaluate(AgentCapability.WriteFile, "C:/repo/.git/hooks/pre-commit");
        Assert.Equal(ToolRiskLevel.Destructive, risk);
        Assert.Equal("security-configuration", rule!.Name);
    }

    [Fact]
    public void WriteFile_to_policy_json_escalates_to_Destructive()
    {
        var (risk, _) = ToolEscalation.Evaluate(AgentCapability.WriteFile, "C:/tolik/policy.json");
        Assert.Equal(ToolRiskLevel.Destructive, risk);
    }

    [Fact]
    public void WriteFile_to_AuthorizationPolicy_cs_escalates_to_Critical()
    {
        var (risk, rule) = ToolEscalation.Evaluate(AgentCapability.WriteFile, "src/TOLIK.Infrastructure/Authorization/AuthorizationPolicy.cs");
        Assert.Equal(ToolRiskLevel.Critical, risk);
        Assert.Equal("security-critical-source", rule!.Name);
    }

    [Fact]
    public void WriteFile_to_regular_source_does_not_escalate()
    {
        var (_, rule) = ToolEscalation.Evaluate(AgentCapability.WriteFile, "src/App/Program.cs");
        Assert.Null(rule);
    }

    [Fact]
    public void RunCodex_always_escalates_to_Modify()
    {
        var (risk, rule) = ToolEscalation.Evaluate(AgentCapability.RunCodex, "C:/work/project");
        Assert.Equal(ToolRiskLevel.Modify, risk);
        Assert.Equal("codex-autonomy", rule!.Name);
    }

    [Fact]
    public void RunOpenCode_not_covered_by_codex_rule()
    {
        // The codex-autonomy rule targets RunCodex only; RunOpenCode stays at declared risk
        // until its own rule is added (§9: never merge the runners' semantics).
        var (_, rule) = ToolEscalation.Evaluate(AgentCapability.RunOpenCode, "C:/work/project");
        Assert.Null(rule);
    }

    [Fact]
    public void Policy_LowRisk_escalated_to_Destructive_requires_confirmation()
    {
        // Even a LowRisk-declared write to security configuration must demand confirmation.
        var decision = AuthorizationPolicy.Evaluate(Request(AgentCapability.WriteFile, ToolRiskLevel.LowRisk, "repo/.git/hooks/pre-commit"));
        Assert.Equal(AuthorizationDecision.ConfirmationRequired, decision);
    }

    [Fact]
    public void Policy_Critical_escalation_without_mfa_denies()
    {
        var decision = AuthorizationPolicy.Evaluate(
            Request(AgentCapability.WriteFile, ToolRiskLevel.Modify, "src/AuthorizationPolicy.cs", confirmation: true, mfa: false));
        Assert.Equal(AuthorizationDecision.Deny, decision);
    }

    [Fact]
    public void Policy_Critical_escalation_with_mfa_and_confirmation_allows()
    {
        var decision = AuthorizationPolicy.Evaluate(
            Request(AgentCapability.WriteFile, ToolRiskLevel.Modify, "src/AuthorizationPolicy.cs", confirmation: true, mfa: true));
        Assert.Equal(AuthorizationDecision.Allow, decision);
    }

    [Fact]
    public void Policy_ReadOnly_regular_file_still_allows()
    {
        var decision = AuthorizationPolicy.Evaluate(Request(AgentCapability.WriteFile, ToolRiskLevel.ReadOnly, "src/App/Readme.md"));
        Assert.Equal(AuthorizationDecision.Allow, decision);
    }
}
