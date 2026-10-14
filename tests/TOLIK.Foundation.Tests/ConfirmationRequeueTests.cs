using TOLIK.Infrastructure.Tasks;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// The confirmation-park requeue (§9 live-loop fix): the confirmation flow parks a
/// RUNNING task (ConfirmationRequiredException → Pause → Paused) and the designed
/// continuation is the OWNER re-running the execute request — which needs the task
/// PENDING (the execute endpoint's TryStart is Pending → Running; the /resume
/// endpoint's Paused → Running does NOT satisfy it — the live demo hit exactly this
/// seam). The contract: Pause → RequeueForExecution → TryStart succeeds; a direct
/// resume after requeue is refused (the task is no longer Paused).
/// </summary>
public sealed class ConfirmationRequeueTests
{
    [Fact]
    public void Parked_task_requeues_to_pending_and_restarts()
    {
        var lifecycle = new TaskLifecycle();
        var task = lifecycle.Create("confirmation requeue test");

        Assert.True(lifecycle.TryStart(task.Id, out _));           // Pending → Running
        lifecycle.Pause(task.Id, "awaiting_confirmation:x");       // Running → Paused
        Assert.Equal(TolikTaskStatus.Paused, lifecycle.Get(task.Id)!.Status);

        Assert.True(lifecycle.RequeueForExecution(task.Id));       // Paused → Pending
        Assert.Equal(TolikTaskStatus.Pending, lifecycle.Get(task.Id)!.Status);

        // The owner re-runs the execute request: TryStart passes (Pending → Running).
        Assert.True(lifecycle.TryStart(task.Id, out _));
        Assert.True(lifecycle.Complete(task.Id, "steps=2"));
        Assert.Equal(TolikTaskStatus.Completed, lifecycle.Get(task.Id)!.Status);
    }

    [Fact]
    public void Requeue_fails_closed_for_non_parked_states()
    {
        var lifecycle = new TaskLifecycle();
        var task = lifecycle.Create("requeue fail-closed test");

        // Pending: not parked — the requeue is refused (only the confirmation park
        // path requeues; a plain Pending task has nothing to requeue from).
        Assert.False(lifecycle.RequeueForExecution(task.Id));
        Assert.Equal(TolikTaskStatus.Pending, lifecycle.Get(task.Id)!.Status);

        // Running: not parked either.
        Assert.True(lifecycle.TryStart(task.Id, out _));
        Assert.False(lifecycle.RequeueForExecution(task.Id));
        Assert.Equal(TolikTaskStatus.Running, lifecycle.Get(task.Id)!.Status);

        // Unknown id: refused.
        Assert.False(lifecycle.RequeueForExecution(Guid.NewGuid()));
    }

    [Fact]
    public void Resume_still_works_for_owner_pauses_but_blocks_double_requeue()
    {
        var lifecycle = new TaskLifecycle();
        var task = lifecycle.Create("resume semantics test");
        Assert.True(lifecycle.TryStart(task.Id, out _));
        lifecycle.Pause(task.Id, "owner pause");

        // The owner-resume path (Paused → Running) is UNCHANGED by the requeue fix.
        Assert.True(lifecycle.TryResume(task.Id, "resumed_by_owner"));
        Assert.Equal(TolikTaskStatus.Running, lifecycle.Get(task.Id)!.Status);

        // After the resume the task is Running — a requeue is refused (fail closed).
        Assert.False(lifecycle.RequeueForExecution(task.Id));
    }
}
