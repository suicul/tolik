namespace TOLIK.Infrastructure.Persistence;

/// <summary>
/// Durable baseline record (docs/self-evolution.md §2.2, SE-1): a reproducible
/// benchmark result — version / benchmark id / hardware env / software env / metrics
/// (JSON) / test results. Metrics are serialized JSON (name → value); the comparison
/// and reject gate are the deterministic BaselineComparer (TOLIK.Core), not SQL.
/// Baselines are INPUT to the self-optimization gate — never authorization data.
/// </summary>
public sealed class BaselineEntity
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public string BenchmarkId { get; set; } = string.Empty;
    public string HardwareEnv { get; set; } = string.Empty;
    public string SoftwareEnv { get; set; } = string.Empty;
    public string MetricsJson { get; set; } = "{}";
    public string? TestResultsSummary { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
