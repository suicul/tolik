using System.Collections.Concurrent;
namespace TOLIK.Infrastructure.Tasks;
public enum TolikTaskStatus { Pending, Running, Paused, Completed, Failed, Cancelled }
public sealed record TolikTask(Guid Id, string Description, TolikTaskStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);
public sealed record TaskLifecycleEvent(Guid TaskId, string Kind, string? Detail, DateTimeOffset OccurredAt);
public sealed class TaskLifecycle
{
    private const int MaxActiveTasks = 100;
    private readonly ConcurrentDictionary<Guid, TolikTask> tasks = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> cancellations = new();
    private readonly ConcurrentQueue<TaskLifecycleEvent> events = new();
    public TolikTask Create(string description) { if (string.IsNullOrWhiteSpace(description) || description.Length > 4096) throw new ArgumentException("Task description is invalid."); if (tasks.Values.Count(x => x.Status is TolikTaskStatus.Pending or TolikTaskStatus.Running) >= MaxActiveTasks) throw new InvalidOperationException("Task limit reached."); var task = new TolikTask(Guid.NewGuid(), description, TolikTaskStatus.Pending, DateTimeOffset.UtcNow, null); tasks[task.Id] = task; return task; }
    public TolikTask? Get(Guid id) => tasks.TryGetValue(id, out var task) ? task : null;
    public bool TryCancel(Guid id)
    {
        var cancelled = Transition(id, TolikTaskStatus.Pending, TolikTaskStatus.Cancelled) || Transition(id, TolikTaskStatus.Running, TolikTaskStatus.Cancelled);
        if (cancelled)
        {
            if (cancellations.TryGetValue(id, out var source)) source.Cancel();
            Cleanup(id);
            Publish(id, "TaskCancelled", null);
        }
        return cancelled;
    }
    public bool TryStart(Guid id, out CancellationToken cancellationToken)
    {
        cancellationToken = default;
        if (!Transition(id, TolikTaskStatus.Pending, TolikTaskStatus.Running)) return false;
        var source = new CancellationTokenSource();
        cancellations[id] = source;
        cancellationToken = source.Token;
        Publish(id, "TaskStarted", null);
        return true;
    }
    public bool Complete(Guid id, string? detail)
    {
        if (!Transition(id, TolikTaskStatus.Running, TolikTaskStatus.Completed)) return false;
        Cleanup(id);
        Publish(id, "TaskCompleted", detail);
        return true;
    }
    public void Fail(Guid id, string detail) { Transition(id, TolikTaskStatus.Running, TolikTaskStatus.Failed); Cleanup(id); Publish(id, "TaskFailed", detail); }
    /// <summary>Parks a task awaiting an explicit confirmation (§9); re-executable after approval.</summary>
    public void Pause(Guid id, string detail) { Transition(id, TolikTaskStatus.Running, TolikTaskStatus.Paused); Publish(id, "TaskProgress", detail); }
    public void ReportProgress(Guid id, string detail) => Publish(id, "TaskProgress", detail);
    public IReadOnlyList<TaskLifecycleEvent> GetEvents(Guid id) => events.Where(x => x.TaskId == id).ToArray();
    /// <summary>Optional realtime subscriber (WebSocket hub). Never throws on unsubscribe races.</summary>
    public event Action<TaskLifecycleEvent>? EventPublished;
    private bool Transition(Guid id, TolikTaskStatus from, TolikTaskStatus to) => tasks.TryGetValue(id, out var current) && current.Status == from && tasks.TryUpdate(id, current with { Status = to, CompletedAt = DateTimeOffset.UtcNow }, current);
    private void Cleanup(Guid id) { if (cancellations.TryRemove(id, out var source)) source.Dispose(); }
    private void Publish(Guid id, string kind, string? detail)
    {
        var item = new TaskLifecycleEvent(id, kind, detail, DateTimeOffset.UtcNow);
        events.Enqueue(item);
        EventPublished?.Invoke(item);
    }
}
