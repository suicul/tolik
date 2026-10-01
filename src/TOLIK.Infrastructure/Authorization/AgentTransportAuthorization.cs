using TOLIK.Contracts;
using TOLIK.Infrastructure.Identity;

namespace TOLIK.Infrastructure.Authorization;

public sealed record AgentTransportRequest(
    SessionPrincipal? Principal,
    Guid AgentId,
    AgentRegistration Registration,
    AgentCommand Command);

public static class AgentTransportAuthorization
{
    public static AuthorizationDecision Evaluate(AgentTransportRequest request)
    {
        if (request.Principal is null || request.AgentId == Guid.Empty || request.Registration is null || request.Command is null)
            return AuthorizationDecision.Deny;
        if (request.Registration.AgentId != request.AgentId || !request.Registration.Validate().IsValid)
            return AuthorizationDecision.Deny;
        if (!request.Registration.AdvertisedCapabilities.Contains(request.Command.Capability))
            return AuthorizationDecision.Deny;
        return AuthorizationPolicy.Evaluate(new AuthorizationRequest(
            request.Principal,
            request.Command.Capability,
            request.Command.RiskLevel,
            request.AgentId.ToString("D"),
            request.AgentId.ToString("D"),
            ConfirmationValid: false));
    }
}
