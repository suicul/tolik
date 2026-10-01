using System.Collections.Immutable;
using TOLIK.Contracts;
using TOLIK.Infrastructure.Authorization;
using TOLIK.Infrastructure.Identity;

namespace TOLIK.Foundation.Tests;

public sealed class AgentTransportTests
{
    private static SessionPrincipal Principal() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Owner");

    [Fact]
    public void Registered_capability_is_allowed_only_for_authenticated_agent()
    {
        var agent = Guid.NewGuid();
        var registration = new AgentRegistration(agent, ImmutableHashSet.Create(AgentCapability.ReadFile));
        var command = new AgentCommand(Guid.NewGuid(), AgentCapability.ReadFile, ToolRiskLevel.ReadOnly, ImmutableDictionary<string, string>.Empty);
        Assert.Equal(AuthorizationDecision.Allow, AgentTransportAuthorization.Evaluate(new(Principal(), agent, registration, command)));
        Assert.Equal(AuthorizationDecision.Deny, AgentTransportAuthorization.Evaluate(new(null, agent, registration, command)));
    }

    [Fact]
    public void Unadvertised_or_mismatched_agent_is_denied()
    {
        var agent = Guid.NewGuid();
        var registration = new AgentRegistration(agent, ImmutableHashSet.Create(AgentCapability.ReadFile));
        var command = new AgentCommand(Guid.NewGuid(), AgentCapability.WriteFile, ToolRiskLevel.Modify, ImmutableDictionary<string, string>.Empty);
        Assert.Equal(AuthorizationDecision.Deny, AgentTransportAuthorization.Evaluate(new(Principal(), agent, registration, command)));
        Assert.Equal(AuthorizationDecision.Deny, AgentTransportAuthorization.Evaluate(new(Principal(), Guid.NewGuid(), registration, command)));
    }
}
