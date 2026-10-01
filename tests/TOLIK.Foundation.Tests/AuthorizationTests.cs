using TOLIK.Contracts;
using TOLIK.Infrastructure.Authorization;
using TOLIK.Infrastructure.Identity;

namespace TOLIK.Foundation.Tests;

public sealed class AuthorizationPolicyTests
{
    private static SessionPrincipal Principal() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Owner");

    [Fact]
    public void Read_only_requires_authenticated_scoped_context()
    {
        var request = new AuthorizationRequest(Principal(), AgentCapability.ReadFile, ToolRiskLevel.ReadOnly, "project-a", "project-a", false);
        Assert.Equal(AuthorizationDecision.Allow, AuthorizationPolicy.Evaluate(request));
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(request with { Principal = null }));
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(request with { Resource = "project-b" }));
    }

    [Fact]
    public void Destructive_without_confirmation_fails_closed()
    {
        var request = new AuthorizationRequest(Principal(), AgentCapability.WriteFile, ToolRiskLevel.Destructive, "project-a", "project-a", false);
        Assert.Equal(AuthorizationDecision.ConfirmationRequired, AuthorizationPolicy.Evaluate(request));
        Assert.Equal(AuthorizationDecision.Allow, AuthorizationPolicy.Evaluate(request with { ConfirmationValid = true }));
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(request with { PolicyAvailable = false }));
    }

    [Fact]
    public void Expired_confirmation_and_critical_without_mfa_fail_closed()
    {
        var now = DateTimeOffset.Parse("2026-09-19T00:00:00Z");
        var request = new AuthorizationRequest(
            Principal(), AgentCapability.WriteFile, ToolRiskLevel.Destructive, "project-a", "project-a", true,
            EvaluatedAt: now, ConfirmationExpiresAt: now.AddSeconds(-1));

        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(request));
        Assert.Equal(
            AuthorizationDecision.Deny,
            AuthorizationPolicy.Evaluate(request with { Risk = ToolRiskLevel.Critical, ConfirmationExpiresAt = now.AddMinutes(1) }));
    }

    [Fact]
    public void Expired_session_device_scope_or_agent_denies_even_for_read_only()
    {
        var request = new AuthorizationRequest(Principal(), AgentCapability.ReadFile, ToolRiskLevel.ReadOnly, "project-a", "project-a", false);

        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(request with { SessionValid = false }));
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(request with { DeviceValid = false }));
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(request with { TaskScopeValid = false }));
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(request with { AgentAuthorized = false }));
    }
}
