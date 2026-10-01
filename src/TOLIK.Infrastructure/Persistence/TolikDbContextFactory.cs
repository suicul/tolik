using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TOLIK.Infrastructure.Persistence;

public sealed class TolikDbContextFactory : IDesignTimeDbContextFactory<TolikDbContext>
{
    public TolikDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("TOLIK_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Set TOLIK_POSTGRES_CONNECTION before running Entity Framework commands.");
        }

        var options = new DbContextOptionsBuilder<TolikDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new TolikDbContext(options);
    }
}
