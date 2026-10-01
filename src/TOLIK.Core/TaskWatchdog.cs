namespace TOLIK.Core;

/// <summary>
/// Task outcome notification to deliver proactively (§23).
/// Reason keys embed the task id so scheduling is idempotent.
/// </summary>
public sealed record WatchdogNotification(Guid UserId, string Text, string Priority, string Reason);

/// <summary>
/// Pure decision logic for task-driven proactive notifications: failed builds,
/// finished tasks, tasks stuck Running. No I/O — the hosted service polls the
/// task table and deduplicates through <c>ProactiveStore</c>.
/// </summary>
public static class TaskWatchdog
{
    /// <summary>Running longer than this without finishing counts as stuck.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>Only recent outcomes notify — ancient history stays silent.</summary>
    public static readonly TimeSpan OutcomeWindow = TimeSpan.FromHours(1);

    public static WatchdogNotification? ForFailed(Guid userId, Guid taskId, string description)
    {
        if (userId == Guid.Empty || taskId == Guid.Empty) return null;
        return new WatchdogNotification(userId, $"Задача провалена: {Short(description)}", "High", $"task_failed:{taskId:D}");
    }

    public static WatchdogNotification? ForCompleted(Guid userId, Guid taskId, string description)
    {
        if (userId == Guid.Empty || taskId == Guid.Empty) return null;
        return new WatchdogNotification(userId, $"Задача выполнена: {Short(description)}", "Low", $"task_completed:{taskId:D}");
    }

    public static WatchdogNotification? ForStaleRunning(Guid userId, Guid taskId, string description, DateTimeOffset createdAt, DateTimeOffset now)
    {
        if (userId == Guid.Empty || taskId == Guid.Empty) return null;
        if (now - createdAt < StaleAfter) return null;
        return new WatchdogNotification(userId, $"Задача висит в работе уже {(int)(now - createdAt).TotalMinutes} мин: {Short(description)}", "Normal", $"task_stale:{taskId:D}");
    }

    private static string Short(string description)
    {
        var clean = (description ?? string.Empty).Trim();
        if (clean.Length == 0) return "(без описания)";
        return clean.Length <= 200 ? clean : clean[..200] + " …";
    }
}
