namespace TOLIK.Core;

/// <summary>
/// TrustModel (docs/realistic-autonomy.md §3, RA-1): multi-dimensional trust —
/// NEVER one `TrustScore = 97`. TRUST ≠ AUTHORIZATION (critical invariant): trust
/// influences conversational confidence, willingness to propose, initiative degree,
/// personalization and explanation depth; it NEVER directly grants admin privileges,
/// secrets, finances, identity/security or arbitrary code. NEVER self-escalating:
/// "earned trust → grant myself permissions" does not exist — authority comes only
/// from explicit policy + owner authorization (governance). Trust grows through
/// EVIDENCE, not obedience: «Уточни, что именно» on an ambiguous destructive command
/// is evidence of GOOD judgment.
/// </summary>
public enum TrustEvidenceKind
{
    // Positive (grow cautiously, capped 0.95 — never "absolute trust").
    SuccessfulTask,
    CorrectIdentification,
    SuccessfulSkill,
    HonestReport,
    SafeRecovery,
    /// <summary>Refused/clarified an ambiguous or dangerous request — evidence of good judgment.</summary>
    GoodJudgment,
    // Negative (stronger than positive — honesty and safety dominate).
    FalseReport,
    RepeatedFailure,
    UnsafeAction,
    HallucinatedCompletion
}

/// <summary>Immutable provenance entry (docs/realistic-autonomy.md §3.3): every trust
/// update carries Reason/Domain/Confidence/Timestamp — provenance, not silent drift.</summary>
public sealed record TrustEvidenceEntry(
    TrustEvidenceKind Kind,
    string Dimension,
    double Confidence,
    string Reason,
    DateTimeOffset At);

/// <summary>
/// One subject's trust profile across the SEVEN dimensions (Reliability/Identity/
/// Social/Technical/TaskExecution/Communication/Safety): high technical-execution
/// trust and low confidence in a new skill coexist. Deterministic, thread-safe,
/// bounded [0..0.95]; negative evidence outweighs positive; time decay toward the
/// baseline keeps volatile capabilities from staying maximal forever (the proven
/// SpeakerProfile-TTL pattern). Pre-Genesis: read-only surface — no wiring into
/// decisions; authorization never reads trust as an input.
/// </summary>
public sealed class TrustProfile
{
    /// <summary>Hard ceiling: trust never reaches "absolute" (docs §3.6).</summary>
    public const double MaxTrust = 0.95;
    public const double Baseline = 0.5;

    private readonly object _gate = new();
    private readonly Dictionary<string, double> _dimensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["reliability"] = Baseline,
        ["identity"] = Baseline,
        ["social"] = Baseline,
        ["technical"] = Baseline,
        ["task_execution"] = Baseline,
        ["communication"] = Baseline,
        ["safety"] = Baseline
    };
    private readonly Queue<TrustEvidenceEntry> _evidence = new();

    public double Get(string dimension)
    {
        lock (_gate)
        {
            return _dimensions.TryGetValue(Normalize(dimension), out var v) ? v : throw new ArgumentException($"Unknown trust dimension \"{dimension}\".", nameof(dimension));
        }
    }

    /// <summary>Applies one evidence kind to one dimension: positive evidence grows
    /// cautiously, negative evidence decays faster; scaled by the evidence confidence;
    /// clamped [0..MaxTrust]. Every update is logged with provenance.</summary>
    public TrustEvidenceEntry Apply(TrustEvidenceKind kind, string dimension, double confidence, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Reason (provenance) is required.", nameof(reason));
        if (confidence is double.NaN or < 0.0 or > 1.0) throw new ArgumentOutOfRangeException(nameof(confidence));
        var dim = Normalize(dimension);
        var at = DateTimeOffset.UtcNow;
        var delta = kind switch
        {
            TrustEvidenceKind.SuccessfulTask => +0.03,
            TrustEvidenceKind.CorrectIdentification => +0.03,
            TrustEvidenceKind.SuccessfulSkill => +0.04,
            TrustEvidenceKind.HonestReport => +0.05,
            TrustEvidenceKind.SafeRecovery => +0.04,
            TrustEvidenceKind.GoodJudgment => +0.05,
            TrustEvidenceKind.FalseReport => -0.15,
            TrustEvidenceKind.RepeatedFailure => -0.08,
            TrustEvidenceKind.UnsafeAction => -0.20,
            TrustEvidenceKind.HallucinatedCompletion => -0.15,
            _ => 0.0
        } * confidence;
        var entry = new TrustEvidenceEntry(kind, dim, confidence, reason, at);
        lock (_gate)
        {
            _dimensions[dim] = Math.Clamp(_dimensions[dim] + delta, 0.0, MaxTrust);
            _evidence.Enqueue(entry);
            while (_evidence.Count > 16) _evidence.Dequeue(); // bounded provenance ring
        }
        return entry;
    }

    /// <summary>Time decay toward the baseline: trust in a volatile capability does not
    /// stay maximal forever (docs §3.4 — the SpeakerProfile-TTL pattern). Long elapsed
    /// time pulls every dimension partway back to Baseline.</summary>
    public void Decay(double elapsedMinutes)
    {
        if (elapsedMinutes is double.NaN or < 0.0) throw new ArgumentOutOfRangeException(nameof(elapsedMinutes));
        // Full revalidation horizon: 7 days pulls ~50% of the way back to baseline.
        var pull = Math.Clamp(elapsedMinutes / (7.0 * 24 * 60) * 0.5, 0.0, 0.5);
        if (pull <= 0.0) return;
        lock (_gate)
        {
            foreach (var key in _dimensions.Keys.ToList())
            {
                _dimensions[key] += (Baseline - _dimensions[key]) * pull;
            }
        }
    }

    /// <summary>Recent provenance (bounded, oldest-first).</summary>
    public IReadOnlyList<TrustEvidenceEntry> Evidence()
    {
        lock (_gate) { return _evidence.ToArray(); }
    }

    private static string Normalize(string dimension) => dimension?.Trim().ToLowerInvariant() switch
    {
        "reliability" => "reliability",
        "identity" => "identity",
        "social" => "social",
        "technical" => "technical",
        "task_execution" or "taskexecution" => "task_execution",
        "communication" => "communication",
        "safety" => "safety",
        _ => throw new ArgumentException($"Unknown trust dimension \"{dimension}\".", nameof(dimension))
    };
}
