using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TOLIK.Infrastructure.Auditing;
using TOLIK.Infrastructure.Identity;
using TOLIK.Infrastructure.Persistence;
using Xunit;

namespace TOLIK.Core.Host.Tests;

/// <summary>
/// The core lease must be extended WHILE the runtime is healthy. The bug these tests pin:
/// the renewal loop returned early whenever the lease was fresh, so nothing ever extended it,
/// the lease expired after leaseDuration, the runtime fenced itself for a tick and then
/// re-acquired a new epoch — a flap every two minutes in the live logs.
/// </summary>
public sealed class CoreLeaseRenewalTests
{
    /// <summary>Short lease so the loop ticks fast enough to observe in a test (tick = 1s).</summary>
    private static CoreHostFactory ShortLeaseFactory() => new() { LeaseSeconds = 3 };

    [Fact]
    public async Task A_healthy_runtime_keeps_extending_its_lease_without_changing_epoch()
    {
        using var factory = ShortLeaseFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TolikDbContext>();

        CoreLeaseEntity? first = null;
        for (var attempt = 0; attempt < 100 && first is null; attempt++)
        {
            first = await db.CoreLeases.AsNoTracking().SingleOrDefaultAsync();
            if (first is null) { await Task.Delay(50); }
        }
        Assert.NotNull(first);
        DateTimeOffset firstExpiration = first!.LeaseExpiration;
        long firstEpoch = first.Epoch;
        Guid runtime = first.RuntimeInstanceId;

        // Two full lease durations. With the defect the lease expires at ~3s, the runtime goes
        // stale, re-acquires and the epoch increments; with the fix every tick extends it.
        await Task.Delay(TimeSpan.FromSeconds(7));

        using var freshScope = factory.Services.CreateScope();
        var freshDb = freshScope.ServiceProvider.GetRequiredService<TolikDbContext>();
        var after = await freshDb.CoreLeases.AsNoTracking().SingleOrDefaultAsync();

        Assert.NotNull(after);
        Assert.Equal(firstEpoch, after!.Epoch);
        Assert.Equal(runtime, after.RuntimeInstanceId);
        Assert.True(after.LeaseExpiration > firstExpiration,
            $"lease was not extended: {firstExpiration:O} -> {after.LeaseExpiration:O}");
        Assert.True(after.LeaseExpiration > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Renewal_while_healthy_keeps_writing_the_audit_trail()
    {
        using var factory = ShortLeaseFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TolikDbContext>();

        await Task.Delay(TimeSpan.FromSeconds(7));

        var renewals = await db.AuditEvents.AsNoTracking()
            .CountAsync(x => x.EventType == "core.lease.renewed");
        // At least one renewal must have happened without a stale episode preceding it; the
        // renewal loop ticks every leaseDuration/3, so ~2 renewals in 7s is the floor.
        Assert.True(renewals >= 1, $"expected the healthy path to renew, saw {renewals} renewals");

        var staleEvents = await db.AuditEvents.AsNoTracking()
            .CountAsync(x => x.EventType == "core.lease.stale_detected");
        Assert.Equal(0, staleEvents);
    }
}
