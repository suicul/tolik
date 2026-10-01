namespace TOLIK.Contracts;

public enum RetentionClass { Ephemeral, Event, Persistent }
public sealed record HomePerceptionEvent(Guid Id, string Source, string EventType, RetentionClass Retention, DateTimeOffset ObservedAt)
{
    public bool IsValid => Id != Guid.Empty && !string.IsNullOrWhiteSpace(Source) && !string.IsNullOrWhiteSpace(EventType);
}
public sealed record DeploymentHealth(string Component, bool Ready, string Version, DateTimeOffset CheckedAt);
public interface IHomePerceptionStore
{
    Task StoreAsync(HomePerceptionEvent item, CancellationToken cancellationToken = default);
}
