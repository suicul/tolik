using TOLIK.Core;

namespace TOLIK.Foundation.Tests;

public sealed class ProactiveBehaviorTests
{
    [Fact]
    public void Due_messages_are_bounded_and_ordered()
    {
        var scheduler = new ProactiveScheduler(); var user = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        scheduler.Schedule(user, "later", ProactivePriority.Low, now, "test");
        scheduler.Schedule(user, "urgent", ProactivePriority.High, now, "test");
        var due = scheduler.TakeDue(now, 1);
        Assert.Single(due); Assert.Equal("urgent", due[0].Text);
    }

    [Fact]
    public void Failed_task_notifies_high_with_task_key()
    {
        var user = Guid.NewGuid(); var taskId = Guid.NewGuid();
        var notice = TaskWatchdog.ForFailed(user, taskId, "собери проект");
        Assert.NotNull(notice);
        Assert.Equal("High", notice!.Priority);
        Assert.Equal($"task_failed:{taskId:D}", notice.Reason);
        Assert.Contains("собери проект", notice.Text);
    }

    [Fact]
    public void Completed_task_notifies_low()
    {
        var notice = TaskWatchdog.ForCompleted(Guid.NewGuid(), Guid.NewGuid(), "готово");
        Assert.NotNull(notice);
        Assert.Equal("Low", notice!.Priority);
    }

    [Fact]
    public void Only_stale_running_tasks_notify()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Null(TaskWatchdog.ForStaleRunning(Guid.NewGuid(), Guid.NewGuid(), "x", now.AddMinutes(-5), now));
        var stale = TaskWatchdog.ForStaleRunning(Guid.NewGuid(), Guid.NewGuid(), "x", now.AddMinutes(-31), now);
        Assert.NotNull(stale);
        Assert.Equal("Normal", stale!.Priority);
        Assert.Contains("31", stale.Text);
    }

    [Fact]
    public void Empty_principals_and_long_descriptions_are_safe()
    {
        Assert.Null(TaskWatchdog.ForFailed(Guid.Empty, Guid.NewGuid(), "x"));
        Assert.Null(TaskWatchdog.ForCompleted(Guid.NewGuid(), Guid.Empty, "x"));
        var longNotice = TaskWatchdog.ForFailed(Guid.NewGuid(), Guid.NewGuid(), new string('z', 500));
        Assert.NotNull(longNotice);
        Assert.True(longNotice!.Text.Length <= 230);
    }
}
