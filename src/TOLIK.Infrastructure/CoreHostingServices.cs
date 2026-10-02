using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TOLIK.Core;
using TOLIK.Infrastructure.Persistence;
using TOLIK.Infrastructure.Identity;
using TOLIK.Infrastructure.Auditing;

namespace TOLIK.Infrastructure;

public static class CoreHostingServices
{
    public static IServiceCollection AddTolikCoreInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton(CoreRuntime.Describe());
        // Core security state machine (docs/SECURITY.md §27): in-memory, thread-safe,
        // one LOCKED state per process; consumed by IdentityService + the policy sites.
        services.AddSingleton<TOLIK.Infrastructure.Authorization.CoreSecurityState>();
        // Remembered per-user model-routing preference (ask-policy): in-memory singleton.
        services.AddSingleton<TOLIK.Infrastructure.Models.ModelPreferenceStore>();
        // Owner-issued enrollment tokens (§18 trusted-person enrollment): in-memory singleton.
        services.AddSingleton<TOLIK.Infrastructure.Identity.EnrollmentTokenStore>();
        // Signed transport key exchange (stage d part 2): core transport keys + agent peer keys.
        services.AddSingleton<TOLIK.Infrastructure.Transport.CoreTransportKeys>();
        services.AddSingleton<TOLIK.Infrastructure.Transport.PeerRegistry>();
        // Genesis ceremony (docs/genesis.md Phase 6): stateless over the factory — singleton.
        services.AddSingleton<TOLIK.Infrastructure.Genesis.GenesisService>();
        return services;
    }

    public static IServiceCollection AddTolikPostgres(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Postgres must be supplied through secure runtime configuration.");
        }

        services.AddDbContextFactory<TolikDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IdentityService>();
        services.AddScoped<AuditWriter>();
        // Stateless (uses only the singleton DbContextFactory) — safe as singleton;
        // consumed by singleton MemoryCompactionService/DailyReviewService.
        services.AddSingleton<DurableMemoryStore>();
        services.AddScoped<PersonalContextStore>();
        return services;
    }
}
