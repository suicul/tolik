using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TOLIK.Core.Host;
using TOLIK.Core.Host.Tests;
using TOLIK.Infrastructure.Identity;
using TOLIK.Infrastructure.Persistence;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Core lease subsystem (docs/identity-presence.md §1/IP-1): exactly ONE ACTIVE CORE
/// per identity — split-brain prevention is TECHNICAL (single-row lease table +
/// advisory-locked acquisition), staleness is fail-closed (no lease / fenced / expired
/// → stale), and every lease mutation is audited. The test host's CoreLeaseService
/// acquires the startup lease asynchronously — the tests clear the singleton row to
/// exercise the manager directly (the hosted service does not re-acquire for
/// leaseDuration/3 = 40s, so the tests are deterministic).
/// The GuardIntentCapability stale-deny wiring (IsStale → deny at the intent paths) is
/// verified over HTTP: the helper is a static local function in Program.cs and not
/// directly unit-testable — the flag is set on the same CoreLeaseState singleton the
/// guard consults, and the /audio/play intent path must refuse while stale.
/// </summary>
public sealed class CoreLeaseTests
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Waits for the hosted service's startup lease to land, then clears the singleton
    /// row (the service will not re-acquire within leaseDuration/3).
    /// </summary>
    private static async Task<CoreLeaseManager> ResetLeaseRowAsync(IServiceProvider services)
    {
        var manager = services.GetRequiredService<CoreLeaseManager>();
        var db = services.GetRequiredService<TolikDbContext>();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await db.CoreLeases.AsNoTracking().AnyAsync()) break;
            await Task.Delay(50);
        }
        await db.Database.ExecuteSqlRawAsync("DELETE FROM tolik_core_leases");
        return manager;
    }

    [Fact]
    public async Task TryAcquire_fresh_lease_succeeds_with_epoch_one()
    {
        using var factory = new CoreHostFactory();
        using var scope = factory.Services.CreateScope();
        var manager = await ResetLeaseRowAsync(scope.ServiceProvider);

        var runtimeA = Guid.NewGuid();
        var acquisition = await manager.TryAcquireAsync("host-a", runtimeA, LeaseDuration);

        Assert.True(acquisition.Acquired);
        Assert.NotNull(acquisition.Lease);
        Assert.Equal(1, acquisition.Lease!.Epoch);
        Assert.Equal("host-a", acquisition.Lease.ActiveHost);
        Assert.Equal(runtimeA, acquisition.Lease.RuntimeInstanceId);
        Assert.Equal(CoreLeaseManager.StateActive, acquisition.Lease.State);
        // The owner of a live lease is not stale.
        var check = await manager.CheckAsync(runtimeA);
        Assert.False(check.IsStale);
    }

    [Fact]
    public async Task Second_instance_while_active_is_refused_split_brain_prevented()
    {
        using var factory = new CoreHostFactory();
        using var scope = factory.Services.CreateScope();
        var manager = await ResetLeaseRowAsync(scope.ServiceProvider);

        var runtimeA = Guid.NewGuid();
        Assert.True((await manager.TryAcquireAsync("host-a", runtimeA, LeaseDuration)).Acquired);

        // A different runtime while the ACTIVE unexpired lease is held → CONFLICT: the
        // old runtime still holds the lease, the new runtime MUST NOT act.
        var runtimeB = Guid.NewGuid();
        var refused = await manager.TryAcquireAsync("host-b", runtimeB, LeaseDuration);
        Assert.False(refused.Acquired);
        Assert.Equal("CONFLICT", refused.State);
        Assert.NotNull(refused.CurrentLease);
        Assert.Equal(runtimeA, refused.CurrentLease!.RuntimeInstanceId);

        // The lease still belongs to the old runtime; the new runtime is stale.
        Assert.Equal(runtimeA, (await manager.TryGetCurrentLeaseAsync())!.RuntimeInstanceId);
        var check = await manager.CheckAsync(runtimeB);
        Assert.True(check.IsStale);
        Assert.Equal("fenced_by_newer_runtime", check.Reason);
    }

    [Fact]
    public async Task Fence_hands_the_lease_to_the_new_runtime()
    {
        using var factory = new CoreHostFactory();
        using var scope = factory.Services.CreateScope();
        var manager = await ResetLeaseRowAsync(scope.ServiceProvider);

        var runtimeA = Guid.NewGuid();
        Assert.True((await manager.TryAcquireAsync("host-a", runtimeA, LeaseDuration)).Acquired);

        // The migration path: fence the old runtime, acquire the new epoch immediately.
        var runtimeB = Guid.NewGuid();
        var newLease = await manager.FenceAsync(runtimeB, "host-b", LeaseDuration);
        Assert.Equal(2, newLease.Epoch);
        Assert.Equal(runtimeB, newLease.RuntimeInstanceId);
        Assert.Equal("host-b", newLease.ActiveHost);
        Assert.Equal(CoreLeaseManager.StateActive, newLease.State);

        // The old runtime is fenced (stale), the new runtime holds the live lease.
        var oldCheck = await manager.CheckAsync(runtimeA);
        Assert.True(oldCheck.IsStale);
        Assert.Equal("fenced_by_newer_runtime", oldCheck.Reason);
        Assert.False((await manager.CheckAsync(runtimeB)).IsStale);

        // A same-runtime re-acquisition of its own live lease is idempotent (no epoch bump).
        var reacquire = await manager.TryAcquireAsync("host-b", runtimeB, LeaseDuration);
        Assert.True(reacquire.Acquired);
        Assert.Equal(2, reacquire.Lease!.Epoch);
    }

    [Fact]
    public async Task Expired_lease_is_stale_and_a_new_acquire_takes_the_epoch()
    {
        using var factory = new CoreHostFactory();
        using var scope = factory.Services.CreateScope();
        var manager = await ResetLeaseRowAsync(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<TolikDbContext>();

        // A lease with LeaseExpiration in the past (runtime still recorded as ACTIVE).
        var oldRuntime = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.CoreLeases.Add(new CoreLeaseEntity
        {
            Id = CoreLeaseManager.LeaseRowId,
            Epoch = 5,
            ActiveHost = "old-host",
            RuntimeInstanceId = oldRuntime,
            AcquiredAt = now.AddMinutes(-10),
            LeaseExpiration = now.AddMinutes(-5),
            State = CoreLeaseManager.StateActive
        });
        await db.SaveChangesAsync();

        // The expired runtime is stale (fail closed).
        var expiredCheck = await manager.CheckAsync(oldRuntime);
        Assert.True(expiredCheck.IsStale);
        Assert.Equal("lease_expired", expiredCheck.Reason);

        // A new acquire succeeds (expired leases are acquireable) with Epoch + 1.
        var runtimeB = Guid.NewGuid();
        var acquisition = await manager.TryAcquireAsync("host-b", runtimeB, LeaseDuration);
        Assert.True(acquisition.Acquired);
        Assert.Equal(6, acquisition.Lease!.Epoch);

        // The old runtime is now also fenced by the newer runtime's lease.
        var oldCheck = await manager.CheckAsync(oldRuntime);
        Assert.True(oldCheck.IsStale);
        Assert.Equal("fenced_by_newer_runtime", oldCheck.Reason);
    }

    [Fact]
    public async Task Renew_extends_only_for_the_active_runtime()
    {
        using var factory = new CoreHostFactory();
        using var scope = factory.Services.CreateScope();
        var manager = await ResetLeaseRowAsync(scope.ServiceProvider);

        var runtimeA = Guid.NewGuid();
        Assert.True((await manager.TryAcquireAsync("host-a", runtimeA, LeaseDuration)).Acquired);
        var original = (await manager.TryGetCurrentLeaseAsync())!;

        // The ACTIVE runtime extends its own lease.
        Assert.True(await manager.RenewAsync(runtimeA, "host-a", LeaseDuration));
        var renewed = (await manager.TryGetCurrentLeaseAsync())!;
        Assert.Equal(runtimeA, renewed.RuntimeInstanceId);
        Assert.Equal(original.Epoch, renewed.Epoch);
        Assert.True(renewed.LeaseExpiration > original.LeaseExpiration);

        // A different runtime's renewal is refused — a no-op.
        Assert.False(await manager.RenewAsync(Guid.NewGuid(), "host-b", LeaseDuration));
        var after = (await manager.TryGetCurrentLeaseAsync())!;
        Assert.Equal(runtimeA, after.RuntimeInstanceId);
        Assert.Equal(renewed.LeaseExpiration, after.LeaseExpiration);
    }

    [Fact]
    public async Task Lease_operations_are_audited_without_secrets()
    {
        using var factory = new CoreHostFactory();
        using var scope = factory.Services.CreateScope();
        var manager = await ResetLeaseRowAsync(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<TolikDbContext>();

        var runtimeA = Guid.NewGuid();
        Assert.True((await manager.TryAcquireAsync("host-a", runtimeA, LeaseDuration)).Acquired);
        Assert.True(await manager.RenewAsync(runtimeA, "host-a", LeaseDuration));
        await manager.FenceAsync(Guid.NewGuid(), "host-b", LeaseDuration);

        var events = await db.AuditEvents.AsNoTracking()
            .Where(x => x.EventType.StartsWith("core.lease."))
            .Select(x => new { x.EventType, x.Reason })
            .ToListAsync();
        Assert.Contains(events, x => x.EventType == "core.lease.acquired");
        Assert.Contains(events, x => x.EventType == "core.lease.renewed");
        Assert.Contains(events, x => x.EventType == "core.lease.fenced");
        // Reasons carry epoch/host/runtime ids ONLY (no secrets — SECURITY §38).
        Assert.All(events, x =>
        {
            Assert.Contains("epoch:", x.Reason);
            Assert.Contains("runtime:", x.Reason);
        });
    }

    [Fact]
    public async Task Hosted_service_acquires_the_lease_at_startup()
    {
        using var factory = new CoreHostFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TolikDbContext>();

        // The CoreLeaseService claims the lease on host start (host = machine name,
        // runtimeInstanceId = a fresh Guid per process, Epoch 1 on a fresh database).
        CoreLeaseEntity? lease = null;
        for (var attempt = 0; attempt < 100 && lease is null; attempt++)
        {
            lease = await db.CoreLeases.AsNoTracking().SingleOrDefaultAsync();
            if (lease is null) await Task.Delay(50);
        }
        Assert.NotNull(lease);
        Assert.Equal(CoreLeaseManager.StateActive, lease!.State);
        Assert.Equal(1, lease.Epoch);
        Assert.Equal(Environment.MachineName, lease.ActiveHost);
        Assert.NotEqual(Guid.Empty, lease.RuntimeInstanceId);
        Assert.True(lease.LeaseExpiration > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void CoreLeaseState_stale_semantics_fail_closed()
    {
        var state = new CoreLeaseState();
        Assert.False(state.IsStale);
        Assert.Null(state.Reason);
        Assert.Null(state.DetectedAt);

        // Entering STALE is a transition (idempotent — no second transition).
        Assert.True(state.SetStale("fenced_by_newer_runtime"));
        Assert.True(state.IsStale);
        Assert.Equal("fenced_by_newer_runtime", state.Reason);
        Assert.NotNull(state.DetectedAt);
        Assert.False(state.SetStale("lease_expired"));
        Assert.Equal("fenced_by_newer_runtime", state.Reason);

        // Recovery clears the state.
        state.Clear();
        Assert.False(state.IsStale);
        Assert.Null(state.Reason);
        Assert.Null(state.DetectedAt);
    }

    [Fact]
    public async Task Stale_runtime_is_refused_at_intent_paths()
    {
        // GuardIntentCapability wiring (docs/identity-presence.md IP-1): a stale runtime's
        // capability actions are refused at the intent paths — technical fencing. The
        // flag is set on the same CoreLeaseState singleton the guard consults.
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        var token = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CoreLeaseState>().SetStale("test_fencing");

        var response = await client.PostAsJsonAsync("/audio/play", new { category = "system", name = "noop" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Contains("core lease", body.GetProperty("detail").GetString());
    }
}
