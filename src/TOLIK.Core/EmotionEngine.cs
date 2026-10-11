namespace TOLIK.Core;

/// <summary>The slow mood layer (docs/mind.md §4, MI-5 — PLAN REFORM PRG-1b): the
/// valence (the satisfaction−frustration balance [-1..1]), the arousal [0..1], and
/// the momentum (the current trend: positive = improving). Computed — never an LLM
/// output; clamped, no NaN.</summary>
public sealed record MoodState(double Valence, double Arousal, double Momentum);

/// <summary>
/// EmotionEngine (docs/mind.md §4, MI-5 — PLAN REFORM PRG-1b): emotions emerge from
/// the MindState TRANSITIONS — a mechanical reading of how the real drives moved
/// between ticks, NEVER an LLM conclusion and never fabricated (§50). Two layers:
/// PULSES (transient, ~1%/tick decay: a novelty jump → surprise, a satisfaction
/// rise → content, a frustration rise → frustration, a curiosity rise → interest)
/// and the MOOD (the slow EWMA, ~30-minute e-fold: valence/arousal/momentum). The
/// mood is the internal foundation — its wiring to the voice (VI-6) and the
/// expression contract lands with their tracks; honest: unwired ≠ broken.
/// Thread-safe; deterministic given the tick inputs.
/// </summary>
public sealed class EmotionEngine
{
    private const double PulseThreshold = 0.05;
    private const double PulseDecay = 0.99;         // ~1%/tick at 1 Hz
    private const double MoodAlpha = 1.0 / 1800.0;  // ~30-minute e-fold at 1 Hz

    private readonly object _gate = new();
    private MindSnapshot? _previous;
    private double _surprise, _content, _frustration, _interest;
    private double _valenceEwma, _arousalEwma;

    /// <summary>The current slow mood (valence/arousal/momentum — clamped, finite).</summary>
    public MoodState Mood
    {
        get
        {
            lock (_gate)
            {
                var instantaneous = InstantaneousValence(_previous);
                return new MoodState(
                    Clamp(_valenceEwma, -1.0, 1.0),
                    Clamp(_arousalEwma, 0.0, 1.0),
                    Clamp(instantaneous - _valenceEwma, -1.0, 1.0)); // the trend: the current balance vs the settled mood
            }
        }
    }

    /// <summary>A transient pulse intensity [0..1] by its fixed literal name
    /// ("surprise"/"content"/"frustration"/"interest").</summary>
    public double Pulse(string kind)
    {
        lock (_gate)
        {
            return kind switch
            {
                "surprise" => _surprise,
                "content" => _content,
                "frustration" => _frustration,
                "interest" => _interest,
                _ => throw new ArgumentException("Unknown pulse kind (fixed literals only).", nameof(kind))
            };
        }
    }

    /// <summary>Feeds one tick's snapshot (call BEFORE that tick's state updates — the
    /// engine measures the transition from the previous snapshot to this one).</summary>
    public void OnSnapshot(MindSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (_previous is { } prev)
            {
                Rise(ref _surprise, snapshot.Novelty - prev.Novelty);
                Rise(ref _content, snapshot.Satisfaction - prev.Satisfaction);
                Rise(ref _frustration, snapshot.Frustration - prev.Frustration);
                Rise(ref _interest, snapshot.Curiosity - prev.Curiosity);
            }
            _previous = snapshot;

            _surprise *= PulseDecay;
            _content *= PulseDecay;
            _frustration *= PulseDecay;
            _interest *= PulseDecay;

            var valence = InstantaneousValence(snapshot);
            var arousal = ArousalFrom(snapshot);
            _valenceEwma += (valence - _valenceEwma) * MoodAlpha;
            _arousalEwma += (arousal - _arousalEwma) * MoodAlpha;
        }
    }

    private static void Rise(ref double pulse, double delta)
    {
        if (delta > PulseThreshold && double.IsFinite(delta))
            pulse = Math.Clamp(pulse + delta, 0.0, 1.0);
    }

    private static double InstantaneousValence(MindSnapshot? snapshot)
    {
        if (snapshot is null) return 0.0;
        var satisfaction = FiniteOrZero(snapshot.Satisfaction);
        var frustration = FiniteOrZero(snapshot.Frustration);
        return Math.Clamp(satisfaction - frustration, -1.0, 1.0);
    }

    private static double ArousalFrom(MindSnapshot snapshot)
    {
        // The activity level maps to arousal (mind §8 vocabulary); unknown → REST (fail soft).
        var level = snapshot.ActivityLevel switch
        {
            "REST" => 0.1,
            "LOW" => 0.3,
            "NORMAL" => 0.5,
            "FOCUSED" => 0.8,
            "INTENSE" => 1.0,
            _ => 0.1
        };
        return Math.Clamp(level * 0.7 + FiniteOrZero(snapshot.CognitiveLoad) * 0.3, 0.0, 1.0);
    }

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0.0;
    private static double Clamp(double value, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : min;
}
