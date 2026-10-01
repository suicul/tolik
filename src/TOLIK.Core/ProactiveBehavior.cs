namespace TOLIK.Core;

public enum ProactivePriority { Low, Normal, High }
public sealed record ProactiveMessage(Guid Id, Guid UserId, string Text, ProactivePriority Priority, DateTimeOffset DueAt, string Reason);
public sealed class ProactiveScheduler
{
    private readonly PriorityQueue<ProactiveMessage, (DateTimeOffset DueAt, int Priority)> queue = new();
    public ProactiveMessage Schedule(Guid userId, string text, ProactivePriority priority, DateTimeOffset dueAt, string reason)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(reason) || dueAt < DateTimeOffset.UtcNow.AddMinutes(-1)) throw new ArgumentException("Invalid proactive message.");
        var item = new ProactiveMessage(Guid.NewGuid(), userId, text, priority, dueAt, reason);
        queue.Enqueue(item, (dueAt, -(int)priority)); return item;
    }
    public IReadOnlyList<ProactiveMessage> TakeDue(DateTimeOffset now, int limit = 8)
    {
        if (limit is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(limit));
        var result = new List<ProactiveMessage>();
        while (result.Count < limit && queue.TryPeek(out var item, out _) && item.DueAt <= now) { queue.Dequeue(); result.Add(item); }
        return result;
    }
}
