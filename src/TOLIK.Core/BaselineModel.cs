namespace TOLIK.Core;

/// <summary>
/// Baseline store model (docs/self-evolution.md §2.2, SE-1): a reproducible baseline
/// (version, benchmark id, hardware/software env, metrics, test results) and the
/// deterministic comparison + reject gate. New versions are compared against the
/// baseline PER METRIC; unacceptable regressions in latency/reliability/accuracy/
/// memory/CPU/GPU/security/compatibility are REJECTED — the gate is a hard
/// comparison, never an LLM opinion. Deterministic and unit-testable without a DB
/// (AGENTS.md §24); persistence (Postgres, the LearnedRuleStore pattern) is separate.
/// </summary>
public sealed record BaselineRecord(
    int Version,
    string BenchmarkId,
    string HardwareEnv,
    string SoftwareEnv,
    IReadOnlyDictionary<string, double> Metrics,
    string? TestResultsSummary,
    DateTimeOffset CreatedAt);

/// <summary>Per-metric tolerance: a fraction (0.10 = 10% worse tolerated before reject).</summary>
public sealed record MetricThreshold
{
    public string MetricName { get; }
    public double MaxRegressionFraction { get; }

    public MetricThreshold(string metricName, double maxRegressionFraction)
    {
        if (string.IsNullOrWhiteSpace(metricName)) throw new ArgumentException("Metric name is required.", nameof(metricName));
        if (maxRegressionFraction is double.NaN or < 0.0) throw new ArgumentOutOfRangeException(nameof(maxRegressionFraction));
        MetricName = metricName.Trim();
        MaxRegressionFraction = maxRegressionFraction;
    }
}

public sealed record MetricComparison(
    string Metric,
    double BaselineValue,
    double CandidateValue,
    double DeltaFraction,
    bool Regression);

public sealed record ComparisonResult(
    bool Accepted,
    IReadOnlyList<MetricComparison> Metrics,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Deterministic baseline comparison + regression gate (docs/self-evolution.md §2.2):
/// lower-is-better metrics (latency/tokens/CPU) regress when the candidate is worse by
/// more than the tolerated fraction; higher-is-better metrics (accuracy/reliability/
/// cache hit) regress when the candidate drops below. A missing candidate metric for a
/// baseline metric FAILS CLOSED (reject) — never compare a partial run silently.
/// </summary>
public static class BaselineComparer
{
    /// <summary>Default tolerance (±10%) for metrics without an explicit threshold.</summary>
    public const double DefaultTolerance = 0.10;

    public static ComparisonResult Compare(
        BaselineRecord baseline,
        BaselineRecord candidate,
        IReadOnlyList<MetricThreshold>? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        var tolerances = (thresholds ?? [])
            .ToDictionary(t => t.MetricName, t => t.MaxRegressionFraction, StringComparer.OrdinalIgnoreCase);

        var comparisons = new List<MetricComparison>();
        var reasons = new List<string>();
        foreach (var (metricName, baselineValue) in baseline.Metrics)
        {
            if (!candidate.Metrics.TryGetValue(metricName, out var candidateValue))
            {
                comparisons.Add(new MetricComparison(metricName, baselineValue, double.NaN, double.NaN, Regression: true));
                reasons.Add($"metric \"{metricName}\" missing in candidate — fail closed");
                continue;
            }
            // Fail closed on non-finite values: a corrupted benchmark run is REJECTED,
            // never silently accepted (NaN > tolerance is false — the old hole).
            if (!double.IsFinite(baselineValue) || !double.IsFinite(candidateValue))
            {
                comparisons.Add(new MetricComparison(metricName, baselineValue, candidateValue, double.NaN, Regression: true));
                reasons.Add($"metric \"{metricName}\" has a non-finite value — fail closed");
                continue;
            }
            // Fail closed on unknown metrics: only catalog metrics exist (the store
            // enforces this) — a non-catalog name here is a programming error, and the
            // comparison direction must never be guessed.
            var definition = MetricCatalog.Find(metricName);
            var denominator = Math.Max(Math.Abs(baselineValue), 1e-9);
            var delta = (candidateValue - baselineValue) / denominator;
            var tolerance = tolerances.TryGetValue(metricName, out var t) ? t : DefaultTolerance;
            var regressed = definition.LowerIsBetter
                ? delta > tolerance
                : delta < -tolerance;
            comparisons.Add(new MetricComparison(metricName, baselineValue, candidateValue, delta, regressed));
            if (regressed)
                reasons.Add($"\"{metricName}\" regressed {delta:+0.0%;-0.0%} (tolerance ±{tolerance:0.0%})");
        }

        return new ComparisonResult(reasons.Count == 0, comparisons, reasons);
    }
}
