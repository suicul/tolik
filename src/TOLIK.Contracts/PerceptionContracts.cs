namespace TOLIK.Contracts;

public enum WorldEntityKind { Person, Room, Object, Device, Application }
public sealed record PerceptionEntity(Guid Id, WorldEntityKind Kind, string Name, double Confidence)
{
    public bool IsValid => Id != Guid.Empty && !string.IsNullOrWhiteSpace(Name) && Confidence is >= 0 and <= 1;
}
public sealed record PerceptionObservation(string Provider, DateTimeOffset ObservedAt, IReadOnlyList<PerceptionEntity> Entities)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Provider) && Entities.All(entity => entity.IsValid);
}
public sealed record WorldState(IReadOnlyList<PerceptionEntity> Entities, DateTimeOffset UpdatedAt);
public interface IPerceptionService
{
    WorldState Apply(PerceptionObservation observation);
}
