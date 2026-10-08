using TOLIK.Infrastructure.Tasks;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Pause/Resume transitions (docs/governance.md §5): Paused→Running ONLY via TryResume
/// with a fresh CTS; Pending and terminal states never resume. Extends the existing
/// TaskLifecycle (no parallel system).
/// </summary>
public sealed class TaskLifecyclePauseResumeTests
{
    [Fact]
    public void TryResume_from_Paused_returns_true_and_transitions_to_Running()
    {
        var lifecycle = new TaskLifecycle();
        var task = lifecycle.Create("pause-resume smoke");
        Assert.True(lifecycle.TryStart(task.Id, out _));
        lifecycle.Pause(task.Id, "awaiting_confirmation");
        Assert.Equal(TolikTaskStatus.Paused, lifecycle.Get(task.Id)!.Status);
        Assert.True(lifecycle.TryResume(task.Id, "resumed_by_owner"));
        Assert.Equal(TolikTaskStatus.Running, lifecycle.Get(task.Id)!.Status);
    }

    [Fact]
    public void TryResume_from_Pending_returns_false()
    {
        var lifecycle = new TaskLifecycle();
        var task = lifecycle.Create("never started");
        Assert.False(lifecycle.TryResume(task.Id, "resumed_by_owner"));
        Assert.Equal(TolikTaskStatus.Pending, lifecycle.Get(task.Id)!.Status);
    }

    [Fact]
    public void TryResume_from_Completed_returns_false()
    {
        var lifecycle = new TaskLifecycle();
        var task = lifecycle.Create("done");
        Assert.True(lifecycle.TryStart(task.Id, out _));
        Assert.True(lifecycle.Complete(task.Id, "ok"));
        Assert.False(lifecycle.TryResume(task.Id, "resumed_by_owner"));
        Assert.Equal(TolikTaskStatus.Completed, lifecycle.Get(task.Id)!.Status);
    }

    [Fact]
    public void TryResume_publishes_a_TaskResumed_event()
    {
        var lifecycle = new TaskLifecycle();
        var task = lifecycle.Create("event smoke");
        Assert.True(lifecycle.TryStart(task.Id, out _));
        lifecycle.Pause(task.Id, "awaiting_confirmation");
        Assert.True(lifecycle.TryResume(task.Id, "resumed_by_owner"));
        Assert.Contains(lifecycle.GetEvents(task.Id), e => e.Kind is "TaskResumed" && e.Detail == "resumed_by_owner");
    }

    [Fact]
    public void TryResume_gives_a_fresh_cancellation_source()
    {
        var lifecycle = new TaskLifecycle();
        var task = lifecycle.Create("cts smoke");
        Assert.True(lifecycle.TryStart(task.Id, out _));
        lifecycle.Pause(task.Id, "awaiting_confirmation");
        Assert.True(lifecycle.TryResume(task.Id, "resumed_by_owner"));
        // The fresh CTS is registered: cancel after resume still cancels the task.
        Assert.True(lifecycle.TryCancel(task.Id));
        Assert.Equal(TolikTaskStatus.Cancelled, lifecycle.Get(task.Id)!.Status);
    }
}
