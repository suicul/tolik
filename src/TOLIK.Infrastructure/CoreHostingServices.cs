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
