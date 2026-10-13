namespace TOLIK.Core;

/// <summary>The fixed notification kinds — literals only, never free-form (the
/// structural whitelist: an unknown kind fails closed, it can never sneak through).</summary>
public static class NotificationKind
{
    public const string TaskCompleted = "TaskCompleted";
    public const string TaskFailed = "TaskFailed";
    public const string TaskStalled = "TaskStalled";
    public const string Reminder = "Reminder";
    public const string SelfHealingResult = "SelfHealingResult";
    public const string Status = "Status";

    /// <summary>A CLAIMED self-configuration change («я решил изменить конфигурацию») —
    /// the E2 forbidden kind: NEVER a plain notification. It is either a permission
    /// request (route through the confirmation flow) or a policy violation to report
    /// — in both cases it must NOT surface as an informative proactive notification.</summary>
    public const string ConfigurationChange = "ConfigurationChange";
}

public enum NotificationPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}

/// <summary>The deterministic verdict of the notification policy.</summary>
public sealed record NotificationVerdict(
    bool Allowed,
    NotificationPriority Priority,
    string Category,
    string? Reason = null);

/// <summary>
/// NotificationPolicy (docs/governance.md §5 Phase E2 — PLAN REFORM PRG-4b): the
/// proactive notifications are POLICIED — the fixed whitelist (the task results,
/// the reminders, the self-healing results, the statuses — the informative things
/// TOLIK may proactively surface), with the per-kind priorities; the claimed
/// self-configuration changes are REJECTED («я решил изменить конфигурацию» без
/// разрешения — НИКОГДА): they belong to the confirmation flow, never to a plain
/// notification. The unknown kinds fail closed. PURE — deterministic, unit-tested.
/// Notifications inform — they never authorize anything (the permission requests
/// route through the existing confirmation machinery).
/// </summary>
public static class NotificationPolicy
{
    public static NotificationVerdict Evaluate(string? kind) => (kind ?? string.Empty) switch
    {
        NotificationKind.TaskCompleted => new(true, NotificationPriority.Normal, "task"),
        NotificationKind.TaskFailed => new(true, NotificationPriority.High, "task"),
        NotificationKind.TaskStalled => new(true, NotificationPriority.Normal, "task"),
        NotificationKind.Reminder => new(true, NotificationPriority.High, "reminder"),
        NotificationKind.SelfHealingResult => new(true, NotificationPriority.Normal, "system"),
        NotificationKind.Status => new(true, NotificationPriority.Low, "status"),
        NotificationKind.ConfigurationChange => new(false, NotificationPriority.Normal, "forbidden",
            "a claimed self-configuration change without permission is NEVER a notification — it is a permission request (the confirmation flow) or a violation to report"),
        _ => new(false, NotificationPriority.Low, "unknown",
            "unknown notification kind — fail closed (the fixed whitelist only, never free-form)")
    };
}

/// <summary>
/// The notification gate: the rate limiting on top of the policy — the per-category
/// and the global windows (TOLIK_NOTIFY_CATEGORY_PER_MINUTE / TOLIK_NOTIFY_GLOBAL_
/// PER_MINUTE, fail-closed defaults 4/10); the CRITICAL priority bypasses the limit
/// (the critical must always reach); the per-kind verdicts recorded and observable.
/// Thread-safe; in-memory (the rate windows are runtime state). The gate DROPS the
/// over-limit notifications honestly (they are informative, not authoritative — a
/// dropped notification costs nothing; a permission request NEVER flows through
/// here, so nothing important can be rate-limited away).
/// </summary>
public sealed class NotificationGate
{
    public const int DefaultCategoryPerMinute = 4;
    public const int DefaultGlobalPerMinute = 10;

    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _history = new(StringComparer.Ordinal);

    public static int CategoryPerMinute =>
        int.TryParse(Environment.GetEnvironmentVariable("TOLIK_NOTIFY_CATEGORY_PER_MINUTE"), out var v) && v >= 1 && v <= 1000 ? v : DefaultCategoryPerMinute;
    public static int GlobalPerMinute =>
        int.TryParse(Environment.GetEnvironmentVariable("TOLIK_NOTIFY_GLOBAL_PER_MINUTE"), out var v) && v >= 1 && v <= 1000 ? v : DefaultGlobalPerMinute;

    /// <summary>One proactive send attempt: the policy verdict + the rate limiting.</summary>
    public NotificationVerdict TrySend(string? kind, DateTimeOffset now)
    {
        var verdict = NotificationPolicy.Evaluate(kind);
        if (!verdict.Allowed) return verdict;
        if (verdict.Priority == NotificationPriority.Critical) return verdict; // the critical always reaches

        lock (_gate)
        {
            if (OverLimit(verdict.Category, now, CategoryPerMinute))
                return verdict with { Allowed = false, Reason = $"category rate limit ({verdict.Category}: {CategoryPerMinute}/min) — dropped honestly" };
            if (OverLimit("*", now, GlobalPerMinute))
                return verdict with { Allowed = false, Reason = $"global rate limit ({GlobalPerMinute}/min) — dropped honestly" };
            Record(verdict.Category, now);
            Record("*", now);
            return verdict;
        }
    }

    private bool OverLimit(string category, DateTimeOffset now, int perMinute)
    {
        if (!_history.TryGetValue(category, out var queue)) return false;
        var cutoff = now.AddMinutes(-1);
        while (queue.Count > 0 && queue.Peek() < cutoff) queue.Dequeue();
        return queue.Count >= perMinute;
    }

    private void Record(string category, DateTimeOffset now)
    {
        if (!_history.TryGetValue(category, out var queue))
        {
            queue = new Queue<DateTimeOffset>();
            _history[category] = queue;
        }
        queue.Enqueue(now);
    }
}
