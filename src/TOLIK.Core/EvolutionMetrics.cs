namespace TOLIK.Core;

/// <summary>
/// Evolution metrics (docs/self-evolution.md §2.1, SE-0): the measurable basis for
/// self-optimization — a metric exists only when it can be measured deterministically.
/// The catalog is FIXED literals per subsystem (never user content); the recorder is
/// an in-process bounded ring feeding baseline comparison (SE-1). Voice metrics build
/// on the existing M7 telemetry (not a duplicate); Computer Use metrics arrive with
/// CU-GA (post-Genesis). Pre-Genesis: read-only surface — recording only, no decisions.
/// </summary>
public sealed record MetricDefinition(
    string Name,
    string Subsystem,
    string Unit,
    bool LowerIsBetter);

public static class MetricCatalog
{
    /// <summary>Fixed metric definitions (docs/self-evolution.md §2.1). Voice: the M7
    /// telemetry stages; Core: API/DB/serialization/IPC/allocations/startup; Model
    /// usage: tokens/calls/context/cache/reasoning; System: CPU/RAM/disk.</summary>
    public static readonly IReadOnlyList<MetricDefinition> All =
    [
        // Voice (extends M7 telemetry — the stage expansion, not a duplicate).
        new("voice_stt_latency_ms", "voice", "ms", LowerIsBetter: true),
        new("voice_speaker_verify_latency_ms", "voice", "ms", LowerIsBetter: true),
        new("voice_intent_latency_ms", "voice", "ms", LowerIsBetter: true),
        new("voice_tts_ttfa_ms", "voice", "ms", LowerIsBetter: true),
        new("voice_turn_total_ms", "voice", "ms", LowerIsBetter: true),
        // Core.
        new("core_api_latency_ms", "core", "ms", LowerIsBetter: true),
        new("core_db_query_ms", "core", "ms", LowerIsBetter: true),
        new("core_serialization_ms", "core", "ms", LowerIsBetter: true),
        new("core_ipc_ms", "core", "ms", LowerIsBetter: true),
        new("core_startup_ms", "core", "ms", LowerIsBetter: true),
        // Model usage.
        new("model_tokens_total", "model", "tokens", LowerIsBetter: true),
        new("model_calls", "model", "count", LowerIsBetter: true),
        new("model_context_size", "model", "tokens", LowerIsBetter: true),
        new("model_cache_hit_ratio", "model", "ratio", LowerIsBetter: false),
        new("model_reasoning_cycles", "model", "count", LowerIsBetter: true),
        // System.
        new("system_cpu_percent", "system", "percent", LowerIsBetter: true),
        new("system_ram_mb", "system", "mb", LowerIsBetter: true),
        new("system_disk_io_mb", "system", "mb", LowerIsBetter: true),
        // Accuracy/reliability (higher is better — regressions there are the worst).
        new("speaker_verify_accuracy", "voice", "ratio", LowerIsBetter: false),
        new("intent_accuracy", "voice", "ratio", LowerIsBetter: false)
    ];

    private static readonly Dictionary<string, MetricDefinition> Index =
        All.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds a definition; unknown names fail closed (never invent metrics).</summary>
    public static MetricDefinition Find(string name) =>
        Index.TryGetValue(name, out var definition)
            ? definition
            : throw new ArgumentException($"Unknown metric \"{name}\" (catalog is fixed — never invented).", nameof(name));

    public static bool Exists(string name) => Index.ContainsKey(name);
}

/// <summary>One deterministic measurement (the value is measured, never the model's opinion).</summary>
public sealed record MetricObservation(
    string Name,
    double Value,
    DateTimeOffset At,
    string? Detail);

/// <summary>Aggregated summary used by baseline comparison (SE-1).</summary>
public sealed record MetricSummary(
    string Name,
    int Count,
    double Min,
    double P50,
    double P95,
    double Max);

/// <summary>
/// In-process metric recorder: observations per metric in bounded rings (drop-oldest —
/// a lost observation costs nothing); thread-safe; never blocks. Summaries give the
/// percentiles the baseline comparison consumes. Pre-Genesis: no wiring — callers
/// record; nothing reads live yet (SE-2/SE-5 wire the loop post-Genesis).
/// </summary>
public sealed class MetricRecorder
{
    public const int RingCapacity = 256;

    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<MetricObservation>> _rings = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string name, double value, string? detail = null)
    {
        MetricCatalog.Find(name); // fail closed on unknown metrics
        if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
        var at = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_rings.TryGetValue(name, out var ring))
            {
                ring = new Queue<MetricObservation>();
                _rings[name] = ring;
            }
            ring.Enqueue(new MetricObservation(name, value, at, detail));
            while (ring.Count > RingCapacity) ring.Dequeue();
        }
    }

    public MetricSummary? Summary(string name)
    {
        MetricCatalog.Find(name);
        lock (_gate)
        {
            if (!_rings.TryGetValue(name, out var ring) || ring.Count == 0) return null;
            var values = ring.Select(o => o.Value).OrderBy(v => v).ToArray();
            return new MetricSummary(
                name,
                values.Length,
                values[0],
                Percentile(values, 0.50),
                Percentile(values, 0.95),
                values[^1]);
        }
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 1) return sorted[0];
        var position = fraction * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper ? sorted[lower] : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}
