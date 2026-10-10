using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Self-evolution pre-Genesis (docs/self-evolution.md SE-0/SE-1): the fixed metric
/// catalog + bounded recorder (deterministic measurements, never the model's opinion),
/// and the baseline comparison + regression gate (hard per-metric comparison, missing
/// metrics fail closed, never an LLM verdict).
/// </summary>
public sealed class MetricCatalogTests
{
    [Fact]
    public void Catalog_is_fixed_and_covers_subsystems()
    {
        Assert.Contains(MetricCatalog.All, m => m.Name == "voice_tts_ttfa_ms" && m.Subsystem == "voice");
        Assert.Contains(MetricCatalog.All, m => m.Name == "core_api_latency_ms" && m.Subsystem == "core");
        Assert.Contains(MetricCatalog.All, m => m.Name == "model_cache_hit_ratio" && !m.LowerIsBetter);
        Assert.Contains(MetricCatalog.All, m => m.Name == "speaker_verify_accuracy" && !m.LowerIsBetter);
    }

    [Fact]
    public void Unknown_metric_fails_closed()
    {
        Assert.Throws<ArgumentException>(() => MetricCatalog.Find("vibes"));
        Assert.False(MetricCatalog.Exists("vibes"));
    }
}

public sealed class MetricRecorderTests
{
    [Fact]
    public void Summaries_give_percentiles()
    {
        var recorder = new MetricRecorder();
        for (var i = 1; i <= 100; i++) recorder.Record("core_api_latency_ms", i);
        var summary = recorder.Summary("core_api_latency_ms");
        Assert.NotNull(summary);
        Assert.Equal(100, summary.Count);
        Assert.Equal(1, summary.Min, 6);
        Assert.Equal(100, summary.Max, 6);
        Assert.Equal(50.5, summary.P50, 6);
        Assert.Equal(95.05, summary.P95, 6);
    }

    [Fact]
    public void Ring_is_bounded_drop_oldest()
    {
        var recorder = new MetricRecorder();
        for (var i = 0; i < 500; i++) recorder.Record("core_db_query_ms", i);
        var summary = recorder.Summary("core_db_query_ms");
        Assert.NotNull(summary);
        Assert.Equal(MetricRecorder.RingCapacity, summary.Count); // 256 kept
        Assert.Equal(244, summary.Min, 6); // oldest dropped
        Assert.Equal(499, summary.Max, 6);
    }

    [Fact]
    public void Unknown_metric_and_nan_fail_closed()
    {
        var recorder = new MetricRecorder();
        Assert.Throws<ArgumentException>(() => recorder.Record("vibes", 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => recorder.Record("core_api_latency_ms", double.NaN));
        Assert.Null(recorder.Summary("core_ipc_ms")); // nothing recorded
    }
}

public sealed class BaselineComparerTests
{
    private static BaselineRecord Record(int version, IReadOnlyDictionary<string, double> metrics) =>
        new(version, "bench-tts", "rtx3060/win11", "dotnet10", metrics, "all green", DateTimeOffset.UtcNow);

    [Fact]
    public void No_regressions_accepted()
    {
        var baseline = Record(1, new Dictionary<string, double> { ["voice_tts_ttfa_ms"] = 100, ["speaker_verify_accuracy"] = 0.9 });
        var candidate = Record(2, new Dictionary<string, double> { ["voice_tts_ttfa_ms"] = 105, ["speaker_verify_accuracy"] = 0.92 });
        var result = BaselineComparer.Compare(baseline, candidate);
        Assert.True(result.Accepted);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void Latency_regression_beyond_tolerance_rejected()
    {
        var baseline = Record(1, new Dictionary<string, double> { ["voice_tts_ttfa_ms"] = 100 });
        var candidate = Record(2, new Dictionary<string, double> { ["voice_tts_ttfa_ms"] = 115 }); // +15% > +10%
        var result = BaselineComparer.Compare(baseline, candidate);
        Assert.False(result.Accepted);
        Assert.Contains(result.Reasons, r => r.Contains("voice_tts_ttfa_ms"));
    }

    [Fact]
    public void Latency_within_tolerance_accepted()
    {
        var baseline = Record(1, new Dictionary<string, double> { ["voice_tts_ttfa_ms"] = 100 });
        var candidate = Record(2, new Dictionary<string, double> { ["voice_tts_ttfa_ms"] = 110 }); // exactly +10%
        Assert.True(BaselineComparer.Compare(baseline, candidate).Accepted);
        var slightlyWorse = Record(2, new Dictionary<string, double> { ["voice_tts_ttfa_ms"] = 111 }); // +11%
        Assert.False(BaselineComparer.Compare(baseline, slightlyWorse).Accepted);
    }

    [Fact]
    public void Accuracy_drop_rejected_higher_is_better()
    {
        var baseline = Record(1, new Dictionary<string, double> { ["speaker_verify_accuracy"] = 0.90 });
        var candidate = Record(2, new Dictionary<string, double> { ["speaker_verify_accuracy"] = 0.78 }); // −13% < −10%
        var result = BaselineComparer.Compare(baseline, candidate);
        Assert.False(result.Accepted);
        // Improvement in accuracy is fine.
        var better = Record(2, new Dictionary<string, double> { ["speaker_verify_accuracy"] = 0.95 });
        Assert.True(BaselineComparer.Compare(baseline, better).Accepted);
    }

    [Fact]
    public void Missing_candidate_metric_fails_closed()
    {
        var baseline = Record(1, new Dictionary<string, double> { ["voice_turn_total_ms"] = 500, ["core_api_latency_ms"] = 10 });
        var candidate = Record(2, new Dictionary<string, double> { ["voice_turn_total_ms"] = 500 }); // core_api missing
        var result = BaselineComparer.Compare(baseline, candidate);
        Assert.False(result.Accepted); // never compare a partial run silently
        Assert.Contains(result.Reasons, r => r.Contains("missing in candidate"));
    }

    [Fact]
    public void Explicit_thresholds_override_default()
    {
        var baseline = Record(1, new Dictionary<string, double> { ["model_tokens_total"] = 1000 });
        var candidate = Record(2, new Dictionary<string, double> { ["model_tokens_total"] = 1200 }); // +20% > default ±10%
        Assert.False(BaselineComparer.Compare(baseline, candidate).Accepted);
        var tolerant = new[] { new MetricThreshold("model_tokens_total", 0.25) };
        Assert.True(BaselineComparer.Compare(baseline, candidate, tolerant).Accepted);
    }

    [Fact]
    public void Non_finite_values_fail_closed()
    {
        // M4 (audit): a corrupted benchmark run (NaN/Infinity) is REJECTED, never
        // silently accepted (NaN > tolerance is false — the old fail-open hole).
        var nanBaseline = Record(1, new Dictionary<string, double> { ["voice_tts_ttfa_ms"] = double.NaN });
        var candidate = Record(2, new Dictionary<string, double> { ["voice_tts_ttfa_ms"] = 100 });
        var result = BaselineComparer.Compare(nanBaseline, candidate);
        Assert.False(result.Accepted);
        Assert.Contains(result.Reasons, r => r.Contains("non-finite"));
        var infCandidate = Record(2, new Dictionary<string, double> { ["voice_tts_ttfa_ms"] = double.PositiveInfinity });
        var finiteBaseline = Record(1, new Dictionary<string, double> { ["voice_tts_ttfa_ms"] = 100 });
        var result2 = BaselineComparer.Compare(finiteBaseline, infCandidate);
        Assert.False(result2.Accepted);
        Assert.Contains(result2.Reasons, r => r.Contains("non-finite"));
    }

    [Fact]
    public void Zero_baseline_denominator_is_safe()
    {
        var baseline = Record(1, new Dictionary<string, double> { ["core_serialization_ms"] = 0 });
        var candidate = Record(2, new Dictionary<string, double> { ["core_serialization_ms"] = 0.5 });
        var result = BaselineComparer.Compare(baseline, candidate); // no divide-by-zero
        Assert.False(result.Accepted); // 0 → 0.5 is a real regression for latency
    }
}
