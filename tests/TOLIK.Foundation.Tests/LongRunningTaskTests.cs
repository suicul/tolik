using TOLIK.Infrastructure.Identity;
using TOLIK.Infrastructure.Tasks;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Long-running task foundation (docs/continuous-autonomy.md CA-0/CA-1/CA-4): the
/// explicit bounded TaskScope (no unlimited authority — «do what is necessary» never
/// silently expands), the TaskAuthorizationLease (fail-closed, expiry/revocation; the
/// exact creation context retained — never reinterpreted by personality/memory), and
/// the structured task journal (safe summaries only — never transcripts/secrets).
/// </summary>
public sealed class LongRunningTaskTests
{
    private static TaskScope Scope(string project = "grilyazh", params string[] forbidden) => new(
        project, "доведи проект до конца",
        ["RunBuild", "RunTests", "ReadFile"], ["C:\\DEV\\grilyazh"],
        TimeSpan.FromHours(8), null, forbidden,
        ["build_failed_permanently", "owner_cancelled"], "все тесты зелёные");

    [Fact]
    public void TaskLease_validate_allows_listed_capability_on_live_lease()
    {
        var store = new TaskLeaseStore();
        var lease = store.Issue(Guid.NewGuid(), ["RunBuild", "RunTests"], "grilyazh");
        Assert.True(store.Validate(lease.TaskId, "RunBuild").Allowed);
        Assert.True(store.Validate(lease.TaskId, "runbuild").Allowed); // case-insensitive
    }

    [Fact]
    public void TaskLease_forbidden_operation_denies_even_if_listed()
    {
        var store = new TaskLeaseStore();
        var lease = store.Issue(Guid.NewGuid(), ["RunBuild", "RunCodex"], "grilyazh", forbiddenOperations: ["RunCodex"]);
        var validation = store.Validate(lease.TaskId, "RunCodex");
        Assert.False(validation.Allowed);
        Assert.Equal("forbidden_operation", validation.Reason);
    }

    [Fact]
    public void TaskLease_outside_allowlist_denies()
    {
        var store = new TaskLeaseStore();
        var lease = store.Issue(Guid.NewGuid(), ["RunBuild"], "grilyazh");
        var validation = store.Validate(lease.TaskId, "RunOpenCode");
        Assert.False(validation.Allowed);
        Assert.Equal("outside_task_scope", validation.Reason);
    }

    [Fact]
    public void TaskLease_expires_and_expired_lease_authorizes_nothing()
    {
        var store = new TaskLeaseStore();
        var now = DateTimeOffset.UtcNow;
        var lease = store.Issue(Guid.NewGuid(), ["RunBuild"], "grilyazh", TimeSpan.FromHours(1), now: now);
        Assert.True(store.Validate(lease.TaskId, "RunBuild", now.AddMinutes(30)).Allowed);
        Assert.False(store.Validate(lease.TaskId, "RunBuild", now.AddHours(2)).Allowed);
        Assert.Equal("lease_expired", store.Validate(lease.TaskId, "RunBuild", now.AddHours(2)).Reason);
    }

    [Fact]
    public void TaskLease_revocation_denies_next_dispatch_but_preserves_state()
    {
        var store = new TaskLeaseStore();
        var lease = store.Issue(Guid.NewGuid(), ["RunBuild"], "grilyazh");
        Assert.True(store.Revoke(lease.TaskId, "revoked_by_owner")); // «Толик, прекращай»
        Assert.False(store.Validate(lease.TaskId, "RunBuild").Allowed);
        Assert.Equal("lease_revoked", store.Validate(lease.TaskId, "RunBuild").Reason);
        Assert.NotNull(store.Get(lease.TaskId)); // the context preserved for inspection
        Assert.True(store.Revoke(lease.TaskId, "again")); // idempotent
    }

    [Fact]
    public void TaskLease_unknown_task_fails_closed()
    {
        var store = new TaskLeaseStore();
        var validation = store.Validate(Guid.NewGuid(), "RunBuild");
        Assert.False(validation.Allowed);
        Assert.Equal("no_task_lease", validation.Reason);
        Assert.Null(store.Get(Guid.NewGuid()));
        Assert.False(store.Revoke(Guid.NewGuid(), "x")); // unknown → nothing to revoke
    }

    [Fact]
    public void TaskLease_null_allowlist_authorizes_nothing_fail_closed()
    {
        var store = new TaskLeaseStore();
        var lease = store.Issue(Guid.NewGuid(), null!, "grilyazh");
        Assert.Empty(lease.AllowedCapabilities);
        Assert.False(store.Validate(lease.TaskId, "RunBuild").Allowed);
    }

    [Fact]
    public void TaskJournal_renders_chronologically_with_results()
    {
        var entries = new[]
        {
            new TaskJournalEntry(new DateTimeOffset(2026, 10, 10, 14, 21, 0, TimeSpan.Zero), "build failed", null, null, "compile error", "inspect output"),
            new TaskJournalEntry(new DateTimeOffset(2026, 10, 10, 14, 44, 0, TimeSpan.Zero), "unit tests passed", null, "12/12 green", null, null),
            new TaskJournalEntry(new DateTimeOffset(2026, 10, 10, 14, 31, 0, TimeSpan.Zero), "root cause identified", "fixed API mismatch", null, null, "rerun build"),
        };
        var rendered = TaskJournal.Render(entries);
        Assert.Contains("14:21 — build failed", rendered);
        Assert.Contains("14:31 — root cause identified", rendered); // Action isn't rendered (Result is)
        Assert.Contains("14:44 — unit tests passed → 12/12 green", rendered);
        var lines = rendered.Split(Environment.NewLine);
        Assert.StartsWith("14:21", lines[0]); // chronological despite input order
    }

    [Fact]
    public void TaskJournal_empty_renders_empty()
    {
        Assert.Equal(string.Empty, TaskJournal.Render([]));
        Assert.Equal(string.Empty, TaskJournal.Render(null!));
    }

    [Fact]
    public void Lifecycle_attachment_round_trip()
    {
        var lifecycle = new TaskLifecycle();
        var task = lifecycle.Create("доведи Грильяж");
        lifecycle.AttachPlan(task.Id, Scope());
        lifecycle.AppendJournal(task.Id, new TaskJournalEntry(DateTimeOffset.UtcNow, "build ok", null, null, null, null));
        lifecycle.AppendJournal(task.Id, new TaskJournalEntry(DateTimeOffset.UtcNow.AddSeconds(1), "tests ok", null, null, null, null));

        Assert.NotNull(lifecycle.GetScope(task.Id));
        Assert.Equal("grilyazh", lifecycle.GetScope(task.Id)!.Project);
        Assert.Equal(2, lifecycle.GetJournal(task.Id).Count);
        Assert.Equal("tests ok", lifecycle.GetJournal(task.Id)[^1].Event);
    }

    [Fact]
    public void Journal_append_is_thread_safe_smoke()
    {
        var lifecycle = new TaskLifecycle();
        var task = lifecycle.Create("parallel journal");
        Parallel.For(0, 200, i => lifecycle.AppendJournal(task.Id, new TaskJournalEntry(DateTimeOffset.UtcNow, $"event {i}", null, null, null, null)));
        Assert.Equal(200, lifecycle.GetJournal(task.Id).Count); // no lost entries
    }

    [Fact]
    public void Progress_snapshot_never_fabricates_completion()
    {
        // §50 no fake progress: a snapshot's OverallPercent comes from evidence; 100 only
        // from the lifecycle's Completed terminal state — the snapshot type carries the
        // percent as data, nothing here auto-completes.
        var snapshot = new TaskProgressSnapshot(87.0, "packaging", 12, "fix packaging", 2, "integration tests passed", "zip layout", 0.7);
        Assert.Equal(87.0, snapshot.OverallPercent);
        Assert.Equal(2, snapshot.FailedAttempts);
    }

    [Fact]
    public void Checkpoint_attach_and_get_newest_wins()
    {
        var lifecycle = new TaskLifecycle();
        var task = lifecycle.Create("checkpointed task");
        Assert.Null(lifecycle.GetCheckpoint(task.Id)); // none yet

        lifecycle.AttachCheckpoint(task.Id, new TaskCheckpoint(
            DateTimeOffset.UtcNow, "build", 40.0, "feature/x", ["src/a.cs"], "inspect", null, "build", null, "evidence"));
        Assert.Equal("build", lifecycle.GetCheckpoint(task.Id)!.Phase);
        Assert.Equal(40.0, lifecycle.GetCheckpoint(task.Id)!.OverallPercent);

        // The newest wins (only the LAST checkpoint kept per task).
        lifecycle.AttachCheckpoint(task.Id, new TaskCheckpoint(
            DateTimeOffset.UtcNow, "testing", 80.0, "feature/x", ["src/a.cs"], "build ok", "run tests", "package", "git reset", "evidence"));
        Assert.Equal("testing", lifecycle.GetCheckpoint(task.Id)!.Phase);
        Assert.Equal(80.0, lifecycle.GetCheckpoint(task.Id)!.OverallPercent);
        Assert.Equal("git reset", lifecycle.GetCheckpoint(task.Id)!.RollbackState); // rollback info preserved
    }

    [Fact]
    public void Checkpoint_unknown_task_is_noop_fail_closed()
    {
        var lifecycle = new TaskLifecycle();
        lifecycle.AttachCheckpoint(Guid.NewGuid(), new TaskCheckpoint(DateTimeOffset.UtcNow, "x", 0, null, [], null, null, null, null, null));
        Assert.Null(lifecycle.GetCheckpoint(Guid.NewGuid())); // no-op, no throw
    }
}

/// <summary>
/// Bounded recovery + temporal waits (docs/continuous-autonomy.md CA-6): a failed action
/// is retried ONCE, then an alternate strategy, then STOP+ask — never forever; waits are
/// semantic (condition+timeout) instead of arbitrary sleeps.
/// </summary>
public sealed class RecoveryWaitTests
{
    [Theory]
    [InlineData(0, RecoveryPolicy.RecoveryAction.RetrySame)]
    [InlineData(1, RecoveryPolicy.RecoveryAction.AlternateStrategy)]
    [InlineData(2, RecoveryPolicy.RecoveryAction.EscalatePauseAndAsk)]
    [InlineData(5, RecoveryPolicy.RecoveryAction.EscalatePauseAndAsk)]
    public void Recovery_decides_bounded_attempts(int failedAttempts, RecoveryPolicy.RecoveryAction expected)
    {
        Assert.Equal(expected, RecoveryPolicy.Decide(failedAttempts));
    }

    [Theory]
    [InlineData("connection refused (timeout)", FailureKind.Transient)]
    [InlineData("network unavailable", FailureKind.Transient)]
    [InlineData("compile error CS0246", FailureKind.Permanent)]
    [InlineData("assert failed", FailureKind.Permanent)]
    [InlineData("", FailureKind.Unknown)]
    public void Failure_classification_transient_vs_permanent(string summary, FailureKind expected)
    {
        Assert.Equal(expected, RecoveryPolicy.ClassifyFailure(summary));
    }

    [Fact]
    public async Task WaitUntil_met_before_timeout()
    {
        var flag = false;
        var met = await WaitUntil.ConditionAsync(() => flag, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.False(met); // never set: timeout
        flag = true;
        met = await WaitUntil.ConditionAsync(() => flag, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.True(met); // immediately met
    }

    [Fact]
    public async Task WaitUntil_timeout_returns_false_and_honours_cancellation()
    {
        var met = await WaitUntil.ConditionAsync(() => false, TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.False(met); // the timeout bounds the wait

        using var cts = new CancellationTokenSource();
        var cancelTask = Task.Run(async () => { await Task.Delay(50); cts.Cancel(); });
        var started = DateTimeOffset.UtcNow;
        met = await WaitUntil.ConditionAsync(() => false, TimeSpan.FromSeconds(30), cts.Token);
        Assert.False(met);
        Assert.True((DateTimeOffset.UtcNow - started).TotalSeconds < 5); // the cancellation cut it early
        await cancelTask;
    }
}
