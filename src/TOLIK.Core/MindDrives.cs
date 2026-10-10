namespace TOLIK.Core;

/// <summary>
/// Boredom as a REAL system mechanism (docs/mind.md §3, MI-2): a continuous [0..1]
/// state EMERGING from measurable conditions — never an emotion the LLM selects and
/// never a naive timer («10 minutes = bored» is explicitly wrong). Boredom creates
/// MOTIVATION, not permission: the output feeds candidate-activity generation; the
/// normal authorization pipeline stays mandatory (governance).
/// Formula configurable via TOLIK_MIND_* (the TOLIK_VAD_*/TOLIK_OPT_* pattern);
/// tuned experimentally (self-evolution SE-3).
/// </summary>
public sealed class BoredomModel
{
    // TOLIK_MIND_* configurable weights (defaults: equal contribution, calibrated later).
    // Fail-closed parsing (n4): non-numeric, non-finite or negative values fall back to
    // the default — silent nonsense weighting does not exist.
    public double InactivityWeight { get; } = ParseWeight("TOLIK_MIND_BOREDOM_INACTIVITY_WEIGHT", 1.0);
    public double IdlenessWeight { get; } = ParseWeight("TOLIK_MIND_BOREDOM_IDLENESS_WEIGHT", 1.0);
    public double NoveltyDeficitWeight { get; } = ParseWeight("TOLIK_MIND_BOREDOM_NOVELTY_WEIGHT", 1.0);
    /// <summary>Minutes without meaningful interaction at which the inactivity term saturates.</summary>
    public double InactivitySaturationMinutes { get; } = ParseWeight("TOLIK_MIND_BOREDOM_SATURATION_MIN", 120.0);

    private static double ParseWeight(string variable, double fallback)
    {
        return double.TryParse(Environment.GetEnvironmentVariable(variable), out var value) &&
               double.IsFinite(value) && value > 0.0
            ? value
            : fallback;
    }

    /// <summary>
    /// Computes the continuous boredom level: the weighted sum of the normalized inputs —
    /// inactivity (saturating), idleness (no active tasks), novelty deficit (low novelty),
    /// MINUS the engagement (the meaningful interaction/progress reduces it). Clamped
    /// [0..1]; 0.0 = engaged, 1.0 = strongly seeking activity. Deterministic — the inputs
    /// are the caller's measured values, never the model's opinion.
    /// </summary>
    public double Compute(double minutesSinceMeaningfulInteraction, bool hasActiveTasks, double novelty, bool recentMeaningfulInteraction)
    {
        var inactivity = Math.Clamp(minutesSinceMeaningfulInteraction / Math.Max(1, InactivitySaturationMinutes), 0.0, 1.0);
        var idleness = hasActiveTasks ? 0.0 : 1.0;
        var noveltyDeficit = 1.0 - Math.Clamp(novelty, 0.0, 1.0);
        var engagement = recentMeaningfulInteraction ? 1.0 : 0.0;
        var positive = InactivityWeight * inactivity + IdlenessWeight * idleness + NoveltyDeficitWeight * noveltyDeficit;
        var negative = engagement * Math.Max(1.0, InactivityWeight + IdlenessWeight + NoveltyDeficitWeight) * 0.5;
        return Math.Clamp((positive - negative) / Math.Max(1.0, InactivityWeight + IdlenessWeight + NoveltyDeficitWeight), 0.0, 1.0);
    }
}

/// <summary>
/// Novelty tracker (docs/mind.md §11 novelty model): repeats decay it, new information
/// raises it (a new application ↑↑, an unexpected result ↑↑↑). Feeds curiosity/boredom —
/// a natural activity-selection input. Deterministic; bounded [0..1].
/// </summary>
public sealed class NoveltyTracker
{
    private double _novelty = 0.2; // a small initial level

    /// <summary>Current novelty [0..1].</summary>
    public double Current { get { lock (_gate) { return _novelty; } } }
    private readonly object _gate = new();

    /// <summary>Registers an event kind: repeat → strong decay; new info → rise; a new
    /// application/place → a bigger rise; an unexpected result → the biggest.</summary>
    public void Register(string eventKind)
    {
        lock (_gate)
        {
            _novelty = eventKind switch
            {
                "repeat" => Math.Max(0.0, _novelty - 0.2),
                "new_info" => Math.Min(1.0, _novelty + 0.2),
                "new_application" or "new_place" => Math.Min(1.0, _novelty + 0.4),
                "unexpected" => Math.Min(1.0, _novelty + 0.6),
                _ => _novelty
            };
        }
    }
}

/// <summary>
/// Curiosity model (docs/mind.md §5/§4): triggered by unknown information/contradictions/
/// unexpected results/new software — generates INVESTIGATION GOALS, always bounded by
/// privacy/scope/authorization/resources (mind §5.3: «I wonder what this does» ≠ «open
/// every file»). Deterministic intensity; the goal selection is the Motivation's job.
/// </summary>
public sealed class CuriosityModel
{
    private double _intensity;

    public double Intensity { get { lock (_gate) { return _intensity; } } }
    private readonly object _gate = new();

    /// <summary>Registers a trigger: novelty/contradiction/unexpected raise the intensity;
    /// a satisfied investigation lowers it. Bounded [0..1].</summary>
    public void Register(string trigger)
    {
        lock (_gate)
        {
            _intensity = trigger switch
            {
                "unexpected" or "contradiction" => Math.Min(1.0, _intensity + 0.3),
                "new_info" or "new_application" => Math.Min(1.0, _intensity + 0.2),
                "failed_action" => Math.Min(1.0, _intensity + 0.15),
                "satisfied" => Math.Max(0.0, _intensity - 0.4),
                _ => _intensity
            };
        }
    }
}
