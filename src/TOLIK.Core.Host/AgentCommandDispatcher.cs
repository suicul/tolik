using TOLIK.Contracts;

public sealed class AgentCommandDispatcher(AgentConnectionRegistry registry)
{
    public async Task<string> ExecuteAsync(Guid userId, AgentCapability capability, string argument, CancellationToken cancellationToken)
    {
        // Agents are bound to their owner's userId (§18: no cross-user visibility).
        var agent = registry.GetForUser(userId).FirstOrDefault(item => item.Status == "Connected" && item.Capabilities.Contains(capability));
        if (agent is null)
        {
            Console.WriteLine($"[DISPATCHER] No connected agent with capability {capability} for user {userId}.");
            throw new UnauthorizedAccessException("No connected agent advertises the requested capability.");
        }
        try
        {
            var response = await registry.DispatchRemoteAsync(agent.AgentId, capability, argument, TimeSpan.FromSeconds(120), cancellationToken);
            return response.Outcome switch
            {
                CommandOutcome.Completed when response.Result is not null => response.Result,
                CommandOutcome.Cancelled => throw new OperationCanceledException(cancellationToken),
                _ => throw new InvalidOperationException(response.Error ?? "Agent command failed.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }
}
