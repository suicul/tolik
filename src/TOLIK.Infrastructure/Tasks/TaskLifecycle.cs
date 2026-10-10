using System.Collections.Concurrent;
namespace TOLIK.Infrastructure.Tasks;
public enum TolikTaskStatus { Pending, Running, Paused, Completed, Failed, Cancelled }
public sealed record TolikTask(Guid Id, string Description, TolikTaskStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);
public sealed record TaskLifecycleEvent(Guid TaskId, string Kind, string? Detail, DateTimeOffset OccurredAt);
/// <summary>
/// CA-0 extended long-running state (docs/continuous-autonomy.md §1): a SEPARATE tracked
/// concept layered over the existing <see cref="TolikTaskStatus"/> — the existing enum,
/// transition tries and endpoints are untouched. Phase mirrors the lifecycle status;
/// Scope is the explicit bounded authorization context attached at creation (CA-0/§4);
/// LastJournal is the newest structured journal entry (CA-4/§9); Progress is the
/// evidence-derived snapshot (CA-4/§8). Consumed by the future TaskSupervisor (CA-2).
/// </summary>
public sealed record LongRunningState(string Phase, TaskScope? Scope, TaskJournalEntry? LastJournal, TaskProgressSnapshot? Progress);
public sealed class TaskLifecycle
{
    private const int MaxActiveTasks = 100;
    private readonly ConcurrentDictionary<Guid, TolikTask> tasks = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> cancellations = new();
    private readonly ConcurrentQueue<TaskLifecycleEvent> events = new();
    private readonly ConcurrentDictionary<Guid, LongRunningLog> longRunning = new();

    /// <summary>Per-task long-running attachment: the explicit scope + the structured journal, guarded by one lock.</summary>
    private sealed class LongRunningLog
    {
        public readonly object Gate = new();
        public TaskScope? Scope;
        public readonly List<TaskJournalEntry> Entries = new();
        public TaskCheckpoint? Checkpoint; // CA-3: the last restore point (the newest wins)
    }
    public TolikTask Create(string description) { if (string.IsNullOrWhiteSpace(description) || description.Length > 4096) throw new ArgumentException("Task description is invalid."); if (tasks.Values.Count(x => x.Status is TolikTaskStatus.Pending or TolikTaskStatus.Running) >= MaxActiveTasks) throw new InvalidOperationException("Task limit reached."); var task = new TolikTask(Guid.NewGuid(), description, TolikTaskStatus.Pending, DateTimeOffset.UtcNow, null); tasks[task.Id] = task; longRunning[task.Id] = new LongRunningLog(); return task; }
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
    /// <summary>
    /// Resumes a paused task (docs/governance.md §5): Paused→Running ONLY, with a fresh
    /// CTS (same pattern as TryStart); false for any other state or when MaxActiveTasks
    /// would be exceeded. The reason is published as the TaskResumed event detail.
    /// </summary>
    public bool TryResume(Guid taskId, string reason)
    {
        if (tasks.Values.Count(x => x.Status is TolikTaskStatus.Pending or TolikTaskStatus.Running) >= MaxActiveTasks) return false;
        if (!Transition(taskId, TolikTaskStatus.Paused, TolikTaskStatus.Running)) return false;
        var source = new CancellationTokenSource();
        cancellations[taskId] = source;
        Publish(taskId, "TaskResumed", reason);
        return true;
    }
    public void ReportProgress(Guid id, string detail) => Publish(id, "TaskProgress", detail);
    /// <summary>
    /// Attaches the explicit bounded scope (docs/continuous-autonomy.md CA-0/§4) to a task —
    /// the authorization context is retained exactly as created (CA-0/§15) and never
    /// reinterpreted later by personality/memory. No-op for unknown tasks (fail closed).
    /// The in-memory attachment is the runtime surface; persistence of scope/journal goes
    /// through the endpoints (the existing memory store).
    /// </summary>
    public void AttachPlan(Guid taskId, TaskScope scope)
    {
        if (scope is null) throw new ArgumentNullException(nameof(scope));
        if (!longRunning.TryGetValue(taskId, out var log)) return;
        lock (log.Gate) { log.Scope = scope; }
        Publish(taskId, "TaskPlanAttached", scope.Project);
    }
    /// <summary>
    /// Appends a structured journal entry (CA-4/§9): timestamp/event/action/result/reason/
    /// next step — safe summaries ONLY (never transcripts/secrets). Thread-safe: entries
    /// are appended under the per-task gate; the progress snapshot is re-derived from
    /// evidence. No-op for unknown tasks (fail closed).
    /// </summary>
    public void AppendJournal(Guid taskId, TaskJournalEntry entry)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        if (!longRunning.TryGetValue(taskId, out var log)) return;
        lock (log.Gate) { log.Entries.Add(entry); }
        Publish(taskId, "TaskJournal", entry.Event);
    }
    /// <summary>
    /// The evidence-derived progress snapshot (CA-4/§8) — REAL progress from actual journal
    /// outcomes, never fabricated (§50): a running task NEVER reports 100 (the in-flight/
    /// next step counts as uncompleted work); 100 comes only from the lifecycle's own
    /// Completed terminal state; 0 when no outcomes are observed. Null when nothing is
    /// attached (no scope, no entries).
    /// </summary>
    public TaskProgressSnapshot? GetProgress(Guid taskId)
    {
        if (!longRunning.TryGetValue(taskId, out var log)) return null;
        lock (log.Gate)
        {
            if (log.Scope is null && log.Entries.Count == 0) return null;
            tasks.TryGetValue(taskId, out var task);
            return BuildLongRunningState(task, log.Scope, log.Entries).Progress;
        }
    }
    /// <summary>The attached scope (null when absent/unknown) — the dispatch-time scope gate reads it.</summary>
    public TaskScope? GetScope(Guid taskId)
    {
        if (!longRunning.TryGetValue(taskId, out var log)) return null;
        lock (log.Gate) { return log.Scope; }
    }
    /// <summary>The journal entries in append order (empty when none) — the GET endpoint merges them with the durable memory copy.</summary>
    public IReadOnlyList<TaskJournalEntry> GetJournal(Guid taskId)
    {
        if (!longRunning.TryGetValue(taskId, out var log)) return Array.Empty<TaskJournalEntry>();
        lock (log.Gate) { return log.Entries.ToArray(); }
    }

    /// <summary>
    /// Persists a checkpoint (CA-3/§8): the restore point for crash recovery —
    /// only the LAST checkpoint is kept per task (the newest wins). No-op for unknown
    /// tasks (fail closed). The external-state verification after restore is the
    /// caller's job — the lifecycle stores evidence, it does not verify the world.
    /// </summary>
    public void AttachCheckpoint(Guid taskId, TaskCheckpoint checkpoint)
    {
        if (checkpoint is null) throw new ArgumentNullException(nameof(checkpoint));
        if (!longRunning.TryGetValue(taskId, out var log)) return;
        lock (log.Gate) { log.Checkpoint = checkpoint; }
        Publish(taskId, "TaskCheckpoint", checkpoint.Phase);
    }

    /// <summary>The last checkpoint (null when none) — the resume-after-restart path reads it.</summary>
    public TaskCheckpoint? GetCheckpoint(Guid taskId)
    {
        if (!longRunning.TryGetValue(taskId, out var log)) return null;
        lock (log.Gate) { return log.Checkpoint; }
    }
    public IReadOnlyList<TaskLifecycleEvent> GetEvents(Guid id) => events.Where(x => x.TaskId == id).ToArray();

    /// <summary>
    /// Builds the long-running state from actual evidence (CA-0/CA-4): the phase mirrors
    /// the lifecycle status; the progress percent is DERIVED from the journal outcomes —
    /// 100 ONLY when the lifecycle reports Completed, 0 when no outcomes observed
    /// (§50 no fake progress); the confidence is a deterministic placeholder heuristic
    /// (refined by the future TaskSupervisor CA-2).
    /// </summary>
    private static LongRunningState BuildLongRunningState(TolikTask? task, TaskScope? scope, IReadOnlyList<TaskJournalEntry> entries)
    {
        var phase = task?.Status.ToString() ?? "Unknown";
        var last = entries.Count > 0 ? entries[^1] : null;
        var completedSteps = entries.Count(e => !string.IsNullOrWhiteSpace(e.Result));
        var failedAttempts = entries.Count(e => e.Event.Contains("fail", StringComparison.OrdinalIgnoreCase));
        double percent = task?.Status == TolikTaskStatus.Completed ? 100.0
            : entries.Count == 0 ? 0.0
            : Math.Round(completedSteps * 100.0 / entries.Count, 1);
        var progress = new TaskProgressSnapshot(
            percent, phase, completedSteps, last?.NextStep, failedAttempts,
            entries.LastOrDefault(e => !string.IsNullOrWhiteSpace(e.Result))?.Event,
            CurrentHypothesis: null,
            Confidence: task?.Status == TolikTaskStatus.Completed ? 0.9 : 0.5);
        return new LongRunningState(phase, scope, last, progress);
    }
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
