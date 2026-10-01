using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TOLIK.Infrastructure.Persistence;

namespace TOLIK.Foundation.Tests;

public sealed class PostgresPersistenceTests
{
    [Fact]
    public void DatabaseModelUsesPostgresAndHasInitialMigration()
    {
        var options = new DbContextOptionsBuilder<TolikDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;

        using var context = new TolikDbContext(options);
        var provider = context.Database.ProviderName;
        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys;

        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", provider);
        Assert.Contains(migrations, migration => migration.EndsWith("_InitialPostgres", StringComparison.Ordinal));
        Assert.DoesNotContain("Sqlite", provider, StringComparison.OrdinalIgnoreCase);
    }
}
