using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TOLIK.Infrastructure.Persistence;

public sealed class PostgresReadinessCheck(
    IDbContextFactory<TolikDbContext> contextFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var canConnect = await db.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy("PostgreSQL accepted a connection.")
                : HealthCheckResult.Unhealthy("PostgreSQL did not accept a connection.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL readiness check failed.", exception);
        }
    }
}
