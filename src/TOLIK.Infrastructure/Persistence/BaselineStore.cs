using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TOLIK.Core;

namespace TOLIK.Infrastructure.Persistence;

/// <summary>
/// Durable baseline store (docs/self-evolution.md §2.2, SE-1; the LearnedRuleStore
/// pattern): reproducible baselines with provenance (benchmark id, hardware/software
/// env). Comparison + reject decisions happen in the deterministic BaselineComparer —
/// the store only persists and retrieves. GetLatest gives the CI/benchmark gate the
/// comparison target; nothing here is authorization-relevant. HardwareEnv/
/// SoftwareEnv/TestResultsSummary are DESCRIPTORS ONLY — environment names and test
/// summaries, never secrets, never prompts.
/// </summary>
public sealed class BaselineStore(IDbContextFactory<TolikDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<BaselineRecord> AddAsync(
        int version,
        string benchmarkId,
        string hardwareEnv,
        string softwareEnv,
        IReadOnlyDictionary<string, double> metrics,
        string? testResultsSummary = null,
        CancellationToken cancellationToken = default)
    {
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
        if (string.IsNullOrWhiteSpace(benchmarkId) || benchmarkId.Length > 256) throw new ArgumentException("Benchmark id is required.", nameof(benchmarkId));
        if (string.IsNullOrWhiteSpace(hardwareEnv) || hardwareEnv.Length > 512) throw new ArgumentException("Hardware env is required.", nameof(hardwareEnv));
        if (string.IsNullOrWhiteSpace(softwareEnv) || softwareEnv.Length > 512) throw new ArgumentException("Software env is required.", nameof(softwareEnv));
        ArgumentNullException.ThrowIfNull(metrics);
        foreach (var (name, value) in metrics)
        {
            MetricCatalog.Find(name); // fail closed: only catalog metrics are stored
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(metrics), $"Metric \"{name}\" value must be finite (never NaN/Infinity).");
        }

        var entity = new BaselineEntity
        {
            Id = Guid.NewGuid(),
            Version = version,
            BenchmarkId = benchmarkId.Trim(),
            HardwareEnv = hardwareEnv.Trim(),
            SoftwareEnv = softwareEnv.Trim(),
            MetricsJson = JsonSerializer.Serialize(metrics, JsonOptions),
            TestResultsSummary = testResultsSummary,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Baselines.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    /// <summary>The newest baseline (the comparison target for the next candidate).</summary>
    public async Task<BaselineRecord?> GetLatestAsync(string benchmarkId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(benchmarkId)) throw new ArgumentException("Benchmark id is required.", nameof(benchmarkId));
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Baselines.AsNoTracking()
            .Where(x => x.BenchmarkId == benchmarkId.Trim())
            .OrderByDescending(x => x.Version)
            .ThenByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<BaselineRecord>> GetHistoryAsync(string benchmarkId, int limit = 16, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(benchmarkId)) throw new ArgumentException("Benchmark id is required.", nameof(benchmarkId));
        if (limit is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Baselines.AsNoTracking()
            .Where(x => x.BenchmarkId == benchmarkId.Trim())
            .OrderByDescending(x => x.Version)
            .ThenByDescending(x => x.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return rows.Select(ToRecord).ToList();
    }

    private static BaselineRecord ToRecord(BaselineEntity entity) =>
        new(
            entity.Version,
            entity.BenchmarkId,
            entity.HardwareEnv,
            entity.SoftwareEnv,
            JsonSerializer.Deserialize<IReadOnlyDictionary<string, double>>(entity.MetricsJson, JsonOptions) ?? new Dictionary<string, double>(),
            entity.TestResultsSummary,
            entity.CreatedAt);
}
