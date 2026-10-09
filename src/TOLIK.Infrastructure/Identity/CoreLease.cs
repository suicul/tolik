using Microsoft.EntityFrameworkCore;
using TOLIK.Infrastructure.Auditing;
using TOLIK.Infrastructure.Persistence;

namespace TOLIK.Infrastructure.Identity;

/// <summary>
/// Core lease claim (docs/identity-presence.md §1/IP-1): a snapshot of the single
/// ACTIVE Core lease row. Epoch is monotonic — a runtime observing a lease whose
/// RuntimeInstanceId differs from its own is STALE and must not act. Split-brain
/// prevention is TECHNICAL: the single-row lease table + advisory-locked acquisition
/// inside one database transaction, never a local lock file/PID/mutex/timestamp.
/// </summary>
public sealed record CoreLeaseInfo(
    long Epoch,
    string ActiveHost,
    Guid RuntimeInstanceId,
    DateTimeOffset AcquiredAt,
    DateTimeOffset LeaseExpiration,
    string State);

/// <summary>
/// Result of a lease acquisition attempt: a REFUSED acquisition carries the CONFLICT
/// state plus the current lease — the new runtime must enter the stale state (the old
/// runtime still holds the lease). A successful acquisition returns the new lease.
/// </summary>
public sealed record CoreLeaseAcquisitionResult(bool Acquired, string State, CoreLeaseInfo? CurrentLease, CoreLeaseInfo? Lease)
{
    public static CoreLeaseAcquisitionResult Ok(CoreLeaseInfo lease) => new(true, lease.State, lease, lease);
    public static CoreLeaseAcquisitionResult Conflict(CoreLeaseInfo current) => new(false, "CONFLICT", current, null);
}

/// <summary>
/// Result of a lease check: fail closed — a runtime without a lease, fenced by a
/// newer runtime, or holding an expired lease is STALE (Reason carries the cause).
/// </summary>
public sealed record CoreLeaseCheckResult(bool IsStale, string? Reason, CoreLeaseInfo? Lease)
{
    public static CoreLeaseCheckResult Fresh(CoreLeaseInfo lease) => new(false, null, lease);
    public static CoreLeaseCheckResult Stale(string reason, CoreLeaseInfo? lease) => new(true, reason, lease);
}

/// <summary>
/// Core lease manager (docs/identity-presence.md §1/IP-1): exactly ONE ACTIVE CORE per
/// TOLIK identity. The lease lives in the single-row <c>tolik_core_leases</c> table
/// (identity-protected machinery — part of the protected core, self-evolution §4
/// protected areas). Acquisition is serialized through the
/// <c>pg_advisory_xact_lock(hashtext('tolik.core-lease'))</c> advisory lock inside one
/// database transaction, and every lease mutation is audited at the chain tip via the
/// shared audit-chain advisory lock (same pattern as GenesisService — ceremony and
/// audit-chain semantics unchanged; the injected writer supplies the integrity-hash
/// computation). Audit reasons carry epoch/host/runtime ids ONLY —
/// never secrets.
/// Decisions within spec: a same-runtime re-acquisition of its own unexpired ACTIVE
/// lease is idempotent (no epoch bump, no audit — RenewAsync is the intended path);
/// FenceAsync writes a single "core.lease.fenced" audit event whose reason covers both
/// the fenced (old) and the acquired (new) lease — one event per transaction keeps the
/// audit chain strictly linear, matching the GenesisService/AuditWriter pattern.
/// </summary>
public sealed class CoreLeaseManager(IDbContextFactory<TolikDbContext> dbFactory)
{
    public const string AdvisoryLockKey = "tolik.core-lease";
    public const string StateActive = "ACTIVE";
    public const string StateMigrated = "MIGRATED";
    public const string StateFenced = "FENCED";
    public const string StateExpired = "EXPIRED";

    /// <summary>The lease table is single-row: the singleton sentinel primary key.</summary>
    public const long LeaseRowId = 1L;

    /// <summary>Read-only current-lease lookup (AsNoTracking; null when absent).</summary>
    public async Task<CoreLeaseInfo?> TryGetCurrentLeaseAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var lease = await db.CoreLeases.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return lease is null ? null : Map(lease);
    }

    /// <summary>
    /// Attempts to acquire the Core lease for the given runtime instance. Atomic via the
    /// core-lease advisory lock in one transaction: an ACTIVE unexpired lease held by a
    /// DIFFERENT runtime is a CONFLICT — the new runtime MUST NOT act (split-brain
    /// prevention: the old runtime still holds the lease). An expired/MIGRATED/FENCED/
    /// absent lease is acquired (Epoch + 1, or 1 when absent) with an audit event.
    /// </summary>
    public async Task<CoreLeaseAcquisitionResult> TryAcquireAsync(
        string host, Guid runtimeInstanceId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The core-lease advisory lock serializes acquisition against concurrent
        // runtimes: the split-brain decision happens inside ONE database transaction
        // (docs/identity-presence.md §1) — never by local lock files/PIDs/mutexes.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('tolik.core-lease'))", cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var current = await db.CoreLeases.SingleOrDefaultAsync(cancellationToken);
        if (current is not null && current.State == StateActive && current.LeaseExpiration > now)
        {
            if (current.RuntimeInstanceId == runtimeInstanceId)
            {
                // The same runtime re-acquiring its own live lease: idempotent — the
                // lease is unchanged (no epoch bump, no audit; renewal is the path).
                return CoreLeaseAcquisitionResult.Ok(Map(current));
            }

            // Split-brain prevention: the old runtime still holds the lease — the new
            // runtime MUST NOT act. Refuse; the caller enters the stale state.
            return CoreLeaseAcquisitionResult.Conflict(Map(current));
        }

        // Expired/MIGRATED/FENCED/absent → acquire: the row is written in place (the
        // single-row table) or inserted with the singleton sentinel primary key.
        var epoch = (current?.Epoch ?? 0) + 1;
        var acquiredAt = DateTimeOffset.UtcNow;
        if (current is null)
        {
            current = new CoreLeaseEntity
            {
                Id = LeaseRowId,
                Epoch = epoch,
                ActiveHost = host,
                RuntimeInstanceId = runtimeInstanceId,
                AcquiredAt = acquiredAt,
                LeaseExpiration = acquiredAt + leaseDuration,
                State = StateActive
            };
            db.CoreLeases.Add(current);
        }
        else
        {
            current.Epoch = epoch;
            current.ActiveHost = host;
            current.RuntimeInstanceId = runtimeInstanceId;
            current.AcquiredAt = acquiredAt;
            current.LeaseExpiration = acquiredAt + leaseDuration;
            current.State = StateActive;
        }

        // The shared audit-chain advisory lock (same pattern as GenesisService): the
        // lease event lands atomically at the chain tip, inside the same transaction.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('tolik.audit-chain'))", cancellationToken);
        const string eventType = "core.lease.acquired";
        const string decision = "ALLOW";
        var reason = $"epoch:{epoch};host:{host};runtime:{runtimeInstanceId:D}";
        var occurredAt = DateTimeOffset.UtcNow;
        var correlationId = Guid.NewGuid();
        var previousHash = await db.AuditEvents
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.Id)
            .Select(x => x.IntegrityHash)
            .FirstOrDefaultAsync(cancellationToken);
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            OccurredAt = occurredAt,
            CorrelationId = correlationId,
            EventType = eventType,
            Decision = decision,
            Reason = reason,
            PreviousHash = previousHash,
            IntegrityHash = AuditWriter.ComputeIntegrityHash(correlationId, occurredAt, eventType, decision, reason, previousHash)
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CoreLeaseAcquisitionResult.Ok(Map(current));
    }

    /// <summary>
    /// Reads the current lease and evaluates staleness for the given runtime instance —
    /// fail closed: no lease → stale ("no_lease"); a foreign runtime's lease → stale
    /// ("fenced_by_newer_runtime"); an expired lease → stale ("lease_expired").
    /// Read-only: the staleness audit event is written once per transition by the
    /// lease service, not per check.
    /// </summary>
    public async Task<CoreLeaseCheckResult> CheckAsync(Guid runtimeInstanceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var lease = await db.CoreLeases.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (lease is null)
            return CoreLeaseCheckResult.Stale("no_lease", null);
        var info = Map(lease);
        if (lease.RuntimeInstanceId != runtimeInstanceId)
            return CoreLeaseCheckResult.Stale("fenced_by_newer_runtime", info);
        if (lease.LeaseExpiration <= DateTimeOffset.UtcNow)
            return CoreLeaseCheckResult.Stale("lease_expired", info);
        return CoreLeaseCheckResult.Fresh(info);
    }

    /// <summary>
    /// Extends the lease ONLY when the caller is the ACTIVE runtime (RuntimeInstanceId
    /// match, state ACTIVE, not expired) — else refuses (no-op, returns false).
    /// </summary>
    public async Task<bool> RenewAsync(
        Guid runtimeInstanceId, string host, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('tolik.core-lease'))", cancellationToken);

        var current = await db.CoreLeases.SingleOrDefaultAsync(cancellationToken);
        if (current is null || current.State != StateActive
            || current.LeaseExpiration <= DateTimeOffset.UtcNow
            || current.RuntimeInstanceId != runtimeInstanceId)
            return false;

        current.LeaseExpiration = DateTimeOffset.UtcNow + leaseDuration;
        current.ActiveHost = host;

        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('tolik.audit-chain'))", cancellationToken);
        const string eventType = "core.lease.renewed";
        const string decision = "ALLOW";
        var reason = $"epoch:{current.Epoch};host:{host};runtime:{runtimeInstanceId:D}";
        var occurredAt = DateTimeOffset.UtcNow;
        var correlationId = Guid.NewGuid();
        var previousHash = await db.AuditEvents
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.Id)
            .Select(x => x.IntegrityHash)
            .FirstOrDefaultAsync(cancellationToken);
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            OccurredAt = occurredAt,
            CorrelationId = correlationId,
            EventType = eventType,
            Decision = decision,
            Reason = reason,
            PreviousHash = previousHash,
            IntegrityHash = AuditWriter.ComputeIntegrityHash(correlationId, occurredAt, eventType, decision, reason, previousHash)
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The migration path (docs/identity-presence.md §3): marks the old ACTIVE lease as
    /// FENCED and immediately acquires the new epoch for the new runtime — ONE
    /// transaction, used by the migration script. Unconditional (the migration is the
    /// authorized hand-over): the old runtime goes stale via CheckAsync afterwards.
    /// </summary>
    public async Task<CoreLeaseInfo> FenceAsync(
        Guid newRuntimeInstanceId, string host, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('tolik.core-lease'))", cancellationToken);

        var current = await db.CoreLeases.SingleOrDefaultAsync(cancellationToken);
        var fencedEpoch = current?.Epoch ?? 0;
        var fencedHost = current?.ActiveHost;
        var fencedRuntime = current?.RuntimeInstanceId;

        if (current is not null && current.State == StateActive)
        {
            // Mark the old ACTIVE lease as FENCED — the old runtime loses the right to
            // act (transient within this transaction: the acquire overwrites the row).
            current.State = StateFenced;
        }

        var epoch = fencedEpoch + 1;
        var acquiredAt = DateTimeOffset.UtcNow;
        if (current is null)
        {
            current = new CoreLeaseEntity
            {
                Id = LeaseRowId,
                Epoch = epoch,
                ActiveHost = host,
                RuntimeInstanceId = newRuntimeInstanceId,
                AcquiredAt = acquiredAt,
                LeaseExpiration = acquiredAt + leaseDuration,
                State = StateActive
            };
            db.CoreLeases.Add(current);
        }
        else
        {
            current.Epoch = epoch;
            current.ActiveHost = host;
            current.RuntimeInstanceId = newRuntimeInstanceId;
            current.AcquiredAt = acquiredAt;
            current.LeaseExpiration = acquiredAt + leaseDuration;
            current.State = StateActive;
        }

        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('tolik.audit-chain'))", cancellationToken);
        const string eventType = "core.lease.fenced";
        const string decision = "ALLOW";
        var reason = fencedRuntime is null
            ? $"epoch:{epoch};host:{host};runtime:{newRuntimeInstanceId:D};fenced:absent"
            : $"epoch:{epoch};host:{host};runtime:{newRuntimeInstanceId:D};fenced_epoch:{fencedEpoch};fenced_host:{fencedHost};fenced_runtime:{fencedRuntime:D}";
        var occurredAt = DateTimeOffset.UtcNow;
        var correlationId = Guid.NewGuid();
        var previousHash = await db.AuditEvents
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.Id)
            .Select(x => x.IntegrityHash)
            .FirstOrDefaultAsync(cancellationToken);
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            OccurredAt = occurredAt,
            CorrelationId = correlationId,
            EventType = eventType,
            Decision = decision,
            Reason = reason,
            PreviousHash = previousHash,
            IntegrityHash = AuditWriter.ComputeIntegrityHash(correlationId, occurredAt, eventType, decision, reason, previousHash)
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Map(current);
    }

    private static CoreLeaseInfo Map(CoreLeaseEntity lease) =>
        new(lease.Epoch, lease.ActiveHost, lease.RuntimeInstanceId, lease.AcquiredAt, lease.LeaseExpiration, lease.State);
}
