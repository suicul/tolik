using System.Collections.Concurrent;

namespace TOLIK.Infrastructure.Identity;

/// <summary>
/// The long-running task's authorization lease info (docs/continuous-autonomy.md CA-1/§2):
/// the exact creation-time authorization context — capability allowlist, project, expiry
/// and the explicit forbidden-operations denylist — retained as created (CA-0/§15) and
/// never reinterpreted later by personality/memory.
/// </summary>
public sealed record TaskLeaseInfo(Guid TaskId, string[] AllowedCapabilities, string Project, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, IReadOnlyList<string> ForbiddenOperations);

/// <summary>Fail-closed lease validation result: a denied validation always carries the deterministic reason.</summary>
public sealed record TaskLeaseValidation(bool Allowed, string? Reason)
{
    public static TaskLeaseValidation Ok() => new(true, null);
    public static TaskLeaseValidation Deny(string reason) => new(false, reason);
}

/// <summary>
/// Task authorization lease store (docs/continuous-autonomy.md CA-1/§2): the LONG-RUNNING
/// task's authorization CONTEXT. Issued when a task is created with an explicit scope
/// (expiry = maxDuration ?? the 8h default — expiration returns the task to normal
/// autonomy, never an infinite permission). All decisions are deterministic; fail closed:
/// an unknown, revoked or expired lease authorizes nothing.
/// NOTE: the existing <see cref="TOLIK.Infrastructure.Authorization.AuthorizationLeaseStore"/>
/// (per-step, dispatch-time, fail-closed) remains the ENFORCEMENT — this store is the
/// long-running task's creation context and does NOT replace it (no duplication: every
/// dispatched step still passes the per-step lease + the full guard). The two stores
/// extend each other, per the plan's audit table.
/// </summary>
public sealed class TaskLeaseStore
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(8);
    private readonly ConcurrentDictionary<Guid, TaskLeaseInfo> leases = new();
    private readonly ConcurrentDictionary<Guid, string> revocations = new();

    /// <summary>
    /// Issues the lease for a task/owner with an expiry (maxDuration ?? the 8h default).
    /// `now` is injectable for deterministic tests (same pattern as AuthorizationLeaseStore).
    /// A null capability allowlist is stored as empty — it authorizes nothing (fail closed).
    /// </summary>
    public TaskLeaseInfo Issue(Guid taskId, IReadOnlyList<string> allowedCapabilities, string project, TimeSpan? duration = null, IReadOnlyList<string>? forbiddenOperations = null, DateTimeOffset? now = null)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("Task id is required.", nameof(taskId));
        if (duration is { } parsed && parsed <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be positive.");
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var lease = new TaskLeaseInfo(
            taskId,
            (allowedCapabilities ?? Array.Empty<string>()).ToArray(),
            project ?? string.Empty,
            timestamp,
            timestamp + (duration ?? DefaultDuration),
            (forbiddenOperations ?? Array.Empty<string>()).ToArray());
        leases[taskId] = lease;
        return lease;
    }

    /// <summary>
    /// Fail-closed validation of ONE capability against the task's lease: unknown →
    /// "no_task_lease"; revoked → "lease_revoked"; expired → "lease_expired"; explicitly
    /// forbidden → "forbidden_operation"; outside the capability allowlist →
    /// "outside_task_scope". A listed, non-forbidden capability on a live lease → allowed.
    /// </summary>
    public TaskLeaseValidation Validate(Guid taskId, string capability, DateTimeOffset? now = null)
    {
        if (taskId == Guid.Empty || string.IsNullOrWhiteSpace(capability)) return TaskLeaseValidation.Deny("no_task_lease");
        if (!leases.TryGetValue(taskId, out var lease)) return TaskLeaseValidation.Deny("no_task_lease");
        if (revocations.ContainsKey(taskId)) return TaskLeaseValidation.Deny("lease_revoked");
        if (lease.ExpiresAt <= (now ?? DateTimeOffset.UtcNow)) return TaskLeaseValidation.Deny("lease_expired");
        if (lease.ForbiddenOperations.Contains(capability, StringComparer.OrdinalIgnoreCase)) return TaskLeaseValidation.Deny("forbidden_operation");
        if (!lease.AllowedCapabilities.Contains(capability, StringComparer.OrdinalIgnoreCase)) return TaskLeaseValidation.Deny("outside_task_scope");
        return TaskLeaseValidation.Ok();
    }

    /// <summary>
    /// Revokes the task's lease («Толик, прекращай»): the active work stops per policy —
    /// the next dispatched step is denied ("lease_revoked") — while the task state, journal
    /// and progress are preserved for inspection (CA-1/§16). The reason is retained for
    /// parity with AuthorizationLeaseStore. Idempotent: re-revoking a live lease returns
    /// true; an unknown/absent lease returns false.
    /// </summary>
    public bool Revoke(Guid taskId, string reason)
    {
        if (taskId == Guid.Empty) return false;
        if (!leases.ContainsKey(taskId)) return false;
        revocations[taskId] = reason;
        return true;
    }

    public TaskLeaseInfo? Get(Guid taskId) => leases.TryGetValue(taskId, out var lease) ? lease : null;
}
