using TOLIK.Infrastructure.Auditing;
using TOLIK.Infrastructure.Identity;

namespace TOLIK.Core.Host;

/// <summary>
/// Core lease runtime state (docs/identity-presence.md IP-1; mirrors the CoreSecurityState
/// singleton shape): a STALE runtime is one that lost the Core lease — fenced by a newer
/// runtime, lease expired, or no lease at all (fail closed). STALE is NOT LOCKED: the app
/// keeps running, but this runtime must not act as the active core — the intent-path
/// guard (GuardIntentCapability) refuses capability actions while stale. Recovery is
/// automatic only through the lease itself (renewal or a legitimate re-acquisition of a
/// new epoch); a runtime fenced by a still-ACTIVE newer runtime stays stale.
/// </summary>
public sealed class CoreLeaseState
{
    private readonly object _gate = new();

    public bool IsStale { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset? DetectedAt { get; private set; }

    /// <summary>Enters STALE (idempotent). Returns true when this call caused the transition.</summary>
    public bool SetStale(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Reason is required.", nameof(reason));
        lock (_gate)
        {
            if (IsStale) return false;
            IsStale = true;
            Reason = reason;
            DetectedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    /// <summary>Clears the stale state (the lease was recovered — renewed or re-acquired).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            IsStale = false;
            Reason = null;
            DetectedAt = null;
        }
    }
}

/// <summary>
/// Core lease renewal loop (docs/identity-presence.md IP-1): at startup this process
/// instance acquires the Core lease (host = machine name, runtimeInstanceId = a fresh
/// Guid per process, duration = TOLIK_CORE_LEASE_SEC, default 120s) — on CONFLICT
/// (another active runtime holds the lease) the app still starts but this runtime is
/// marked stale. Every leaseDuration/3 the loop re-checks: a stale runtime attempts
/// renewal, then re-acquisition (expired/absent lease → a new epoch per §4 recovery);
/// a runtime fenced by a still-ACTIVE newer runtime stays stale — TryAcquire refuses
/// with CONFLICT. That is the technical fencing: a fenced runtime's intent actions are
/// refused (wired into GuardIntentCapability only; a DispatchGuardedAsync wiring is a
/// documented follow-up). The service is graceful: it never takes the host down.
/// Audit events carry epoch/host/runtime ids ONLY — never secrets (SECURITY §38).
/// </summary>
public sealed class CoreLeaseService(
    CoreLeaseManager leases,
    CoreLeaseState leaseState,
    IServiceProvider services,
    ILogger<CoreLeaseService> logger) : BackgroundService
{
    /// <summary>Runtime instance identity — fresh Guid per process instance (§1: Runtime ≠ Identity).</summary>
    private readonly Guid _runtimeInstanceId = Guid.NewGuid();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var leaseDuration = ResolveLeaseDuration(services);
        try
        {
            // Startup acquire (docs/identity-presence.md §1): this process claims the
            // lease under its per-process RuntimeInstanceId.
            var acquisition = await leases.TryAcquireAsync(Environment.MachineName, _runtimeInstanceId, leaseDuration, stoppingToken);
            if (acquisition.Acquired)
            {
                logger.LogInformation("Core lease acquired: epoch {Epoch} on {Host} (runtime {Runtime}).",
                    acquisition.Lease!.Epoch, acquisition.Lease.ActiveHost, acquisition.Lease.RuntimeInstanceId);
            }
            else
            {
                // CONFLICT — another active runtime holds the lease (split-brain guard):
                // the app still starts, but this runtime is stale from the first tick.
                var current = acquisition.CurrentLease;
                logger.LogWarning(
                    "CORE LEASE CONFLICT: another active runtime holds the lease (epoch {Epoch}, runtime {Runtime}) — this runtime {Self} is STALE and will not act as the core.",
                    current?.Epoch, current?.RuntimeInstanceId, _runtimeInstanceId);
                leaseState.SetStale($"acquire_conflict:runtime:{current?.RuntimeInstanceId:D};epoch:{current?.Epoch}");
            }

            // Renewal loop: check every leaseDuration/3 (the lease never expires while
            // this process is alive and the database is reachable).
            using var timer = new PeriodicTimer(TimeSpan.FromTicks(Math.Max(leaseDuration.Ticks / 3, 1)));
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RenewalTickAsync(leaseDuration, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            // Graceful: the lease service never takes the host down; the state stays as-is.
            logger.LogWarning("Core lease service stopped: {Message}", ex.Message);
        }
    }

    private async Task RenewalTickAsync(TimeSpan leaseDuration, CancellationToken stoppingToken)
    {
        CoreLeaseCheckResult check;
        try
        {
            check = await leases.CheckAsync(_runtimeInstanceId, stoppingToken);
        }
        catch (Exception ex)
        {
            // Database unreachable — keep the current state, retry next tick.
            logger.LogWarning("Core lease check skipped: {Message}", ex.Message);
            return;
        }
        if (!check.IsStale) return;

        // Stale transition (SetStale returns true only on clear→stale): audited once
        // per transition, not per tick — the audit chain is append-only and precious.
        if (leaseState.SetStale(check.Reason ?? "lease_stale"))
        {
            logger.LogWarning("CORE LEASE STALE: {Reason} — this runtime {Self} stopped acting as the active core.",
                check.Reason, _runtimeInstanceId);
            await AuditStaleAsync(check, stoppingToken);
        }

        // Recover: renew first; a failed renewal falls back to re-acquisition (expired
        // or missing lease → a new epoch per §4 recovery). A runtime fenced by a
        // still-ACTIVE newer runtime stays stale — TryAcquire refuses with CONFLICT.
        try
        {
            if (await leases.RenewAsync(_runtimeInstanceId, Environment.MachineName, leaseDuration, stoppingToken))
            {
                leaseState.Clear();
                logger.LogInformation("Core lease renewed after stale detection (runtime {Self}).", _runtimeInstanceId);
                return;
            }
            var acquisition = await leases.TryAcquireAsync(Environment.MachineName, _runtimeInstanceId, leaseDuration, stoppingToken);
            if (acquisition.Acquired)
            {
                leaseState.Clear();
                logger.LogInformation("Core lease re-acquired after stale detection: epoch {Epoch} (runtime {Self}).",
                    acquisition.Lease!.Epoch, _runtimeInstanceId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Core lease renewal skipped: {Message}", ex.Message);
        }
    }

    private async Task AuditStaleAsync(CoreLeaseCheckResult check, CancellationToken cancellationToken)
    {
        try
        {
            // AuditWriter is a scoped service — a scope per audit keeps the singleton
            // hosted service valid (the VoiceAdaptationService pattern).
            using var scope = services.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<AuditWriter>();
            var lease = check.Lease;
            await audit.WriteAsync(
                Guid.NewGuid(),
                "core.lease.stale_detected",
                "DENY",
                $"reason:{check.Reason};epoch:{lease?.Epoch};host:{lease?.ActiveHost};runtime:{lease?.RuntimeInstanceId:D}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Core lease stale audit skipped: {Message}", ex.Message);
        }
    }

    /// <summary>Lease duration from configuration (TOLIK_CORE_LEASE_SEC, default 120s).</summary>
    private static TimeSpan ResolveLeaseDuration(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var seconds = configuration.GetValue("TOLIK_CORE_LEASE_SEC", 120);
        return seconds is > 0 and <= 24 * 3600 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(120);
    }
}
