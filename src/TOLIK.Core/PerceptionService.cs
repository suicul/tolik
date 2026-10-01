using TOLIK.Contracts;

namespace TOLIK.Core;

public sealed class InMemoryPerceptionService : IPerceptionService
{
    private WorldState state = new([], DateTimeOffset.MinValue);
    public WorldState Apply(PerceptionObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!observation.IsValid) throw new ArgumentException("Invalid perception observation.", nameof(observation));
        state = new WorldState(observation.Entities, observation.ObservedAt);
        return state;
    }
    public WorldState Current => state;
}
