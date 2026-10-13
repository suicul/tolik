namespace TOLIK.Core;

/// <summary>
/// Experiment Lab (docs/self-evolution.md §2.3, SE-2 — PLAN REFORM PRG-7): the
/// SELF-OPTIMIZATION experiment lifecycle — hypothesis → N candidates → benchmark →
/// compare against the SE-1 baseline → accept/reject → experiment memory. The lab
/// is a DETERMINISTIC engine over the validated candidates (the LLM proposes; the
/// benchmark + the baseline decide). The candidates are DATA (the bounded
/// descriptions); nothing here executes anything — the execution is the consumer's
/// job, gated by the authorization pipeline. Bounded (the experiment history is
/// capped); thread-safe.
/// </summary>
public enum ExperimentStatus
{
    Proposed,
    Benchmarking,
    Accepted,
    Rejected
}

public sealed record ExperimentCandidate(
    string Id,
    string Hypothesis,
    string Description,
    double Score,                    // the benchmark score [0..1] (the higher is better)
    DateTimeOffset BenchmarkedAt);

/// <summary>The experiment memory (§2.4): the outcome + the reason — so a rejected
/// optimization is NOT repeated (the lesson persists in the bounded history).</summary>
public sealed record ExperimentRecord(
    string Id,
    string Hypothesis,
    ExperimentStatus Status,
    double BestScore,
    string Reason,
    DateTimeOffset ResolvedAt);

/// <summary>The experiment lab: the bounded experiment lifecycle. Thread-safe.</summary>
public sealed class ExperimentLab
{
    public const int MaxHistory = 128;

    private readonly object _gate = new();
    private readonly List<ExperimentRecord> _history = new();

    /// <summary>Benchmarks N candidates against each other and picks the BEST one —
    /// deterministic (the highest score wins; ties → the first proposed). The winner
    /// must beat the baseline threshold (else ALL rejected). Returns the experiment
    /// record (accepted/rejected + the reason) — the caller acts on it (through the
    /// authorization pipeline). The history is bounded (128, drop-oldest).</summary>
    public ExperimentRecord RunExperiment(
        string id, string hypothesis,
        IReadOnlyList<ExperimentCandidate> candidates,
        double baselineThreshold,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Experiment id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(hypothesis)) throw new ArgumentException("Hypothesis is required.", nameof(hypothesis));
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            var empty = new ExperimentRecord(id, hypothesis, ExperimentStatus.Rejected, 0, "no candidates provided", now);
            AddHistory(empty);
            return empty;
        }

        var best = candidates.OrderByDescending(c => c.Score).First();
        ExperimentRecord record;
        if (best.Score >= baselineThreshold && double.IsFinite(best.Score))
        {
            record = new ExperimentRecord(id, hypothesis, ExperimentStatus.Accepted, best.Score,
                $"accepted: '{best.Description}' scored {best.Score:F3} (baseline {baselineThreshold:F3})", now);
        }
        else
        {
            record = new ExperimentRecord(id, hypothesis, ExperimentStatus.Rejected, best.Score,
                $"rejected: the best score {best.Score:F3} < baseline {baselineThreshold:F3}", now);
        }
        AddHistory(record);
        return record;
    }

    public IReadOnlyList<ExperimentRecord> History()
    {
        lock (_gate) { return _history.ToList(); }
    }

    private void AddHistory(ExperimentRecord record)
    {
        lock (_gate)
        {
            _history.Add(record);
            while (_history.Count > MaxHistory) _history.RemoveAt(0);
        }
    }
}

/// <summary>
/// OptimizationBudget (docs/self-evolution.md §2.5, SE-3 — PLAN REFORM PRG-7):
/// the resource budgets (CPU/GPU/RAM/time) configurable via TOLIK_OPT_* (the
/// TOLIK_VAD_*/TOLIK_OPT_* pattern). The idle preference: the budget is MORE
/// generous when the system is idle, LESS when the user is active. Fail-closed
/// parsing (the invalid values → the defaults). PURE — the budget values only;
/// the enforcement is the consumer's job.
/// </summary>
public sealed class OptimizationBudget
{
    public double MaxCpuPercent { get; }
    public double MaxRamMb { get; }
    public double MaxGpuPercent { get; }
    public TimeSpan MaxBackgroundTimePerHour { get; }
    public bool PreferIdle { get; }

    public OptimizationBudget()
    {
        MaxCpuPercent = ParseBudget("TOLIK_OPT_CPU_PERCENT", 25.0, 1.0, 100.0);
        MaxRamMb = ParseBudget("TOLIK_OPT_RAM_MB", 512.0, 64.0, 8192.0);
        MaxGpuPercent = ParseBudget("TOLIK_OPT_GPU_PERCENT", 10.0, 0.0, 50.0);
        MaxBackgroundTimePerHour = TimeSpan.FromMinutes(ParseBudget("TOLIK_OPT_BACKGROUND_MINUTES", 20.0, 1.0, 60.0));
        PreferIdle = Environment.GetEnvironmentVariable("TOLIK_OPT_PREFER_IDLE")?.ToLowerInvariant() is not "false";
    }

    private static double ParseBudget(string name, double fallback, double min, double max)
    {
        return double.TryParse(Environment.GetEnvironmentVariable(name), out var v) &&
               double.IsFinite(v) && v >= min && v <= max
            ? v
            : fallback;
    }
}
