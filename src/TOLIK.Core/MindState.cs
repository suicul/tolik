namespace TOLIK.Core;

/// <summary>
/// MindState (docs/mind.md §2, MI-0): the persistent internal state, broken into
/// SUBSYSTEMS — never one giant mutable object. Values are continuous [0..1] with
/// discrete states where meaningful. Deterministic updates only (the LLM may propose,
/// the state machine applies — §44/§69: the mind is not the authority). Thread-safe;
/// the snapshot serializes for persistence (Core-backed, versioned — mind.md §7.4).
/// Pre-Genesis: read-only surface (the wiring into autonomous activity is post-Genesis).
/// </summary>
public sealed class MindState
{
    public const int SnapshotVersion = 1;

    private readonly object _gate = new();

    // Continuous subsystem values [0..1] (docs/mind.md §2-§3).
    private double _boredom;          // 0 = engaged ... 1 = strongly seeking activity
    private double _curiosity;        // the intensity of the investigation drive
    private double _novelty;          // the recent-novelty level (repeats decay it)
    private double _frustration;      // repeated failures
    private double _satisfaction;     // successes / praise
    private double _socialDrive;      // the bounded desire for interaction
    private double _cognitiveLoad;    // the current load
    private string _activityLevel = "REST"; // REST/LOW/NORMAL/FOCUSED/INTENSE (mind §8)
    private string? _attentionTarget;       // what attention is on (routing only)
    private string? _uncertaintyNote;       // the explicit «я не знаю» note
    private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;

    public MindSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new MindSnapshot(SnapshotVersion, _boredom, _curiosity, _novelty, _frustration,
                _satisfaction, _socialDrive, _cognitiveLoad, _activityLevel, _attentionTarget,
                _uncertaintyNote, _updatedAt);
        }
    }

    public double Boredom { get { lock (_gate) { return _boredom; } } }
    public double Curiosity { get { lock (_gate) { return _curiosity; } } }
    public double Novelty { get { lock (_gate) { return _novelty; } } }
    public double Frustration { get { lock (_gate) { return _frustration; } } }
    public double Satisfaction { get { lock (_gate) { return _satisfaction; } } }
    public double SocialDrive { get { lock (_gate) { return _socialDrive; } } }
    public string ActivityLevel { get { lock (_gate) { return _activityLevel; } } }
    public string? AttentionTarget { get { lock (_gate) { return _attentionTarget; } } }

    /// <summary>Applies a bounded delta to one named subsystem value; clamped [0..1].
    /// Names are fixed literals from the callers (never user content — §38).</summary>
    public void Adjust(string subsystem, double delta)
    {
        if (string.IsNullOrWhiteSpace(subsystem)) throw new ArgumentException("Subsystem is required.", nameof(subsystem));
        if (double.IsNaN(delta) || double.IsInfinity(delta)) throw new ArgumentOutOfRangeException(nameof(delta));
        lock (_gate)
        {
            switch (subsystem)
            {
                case "boredom": _boredom = Clamp01(_boredom + delta); break;
                case "curiosity": _curiosity = Clamp01(_curiosity + delta); break;
                case "novelty": _novelty = Clamp01(_novelty + delta); break;
                case "frustration": _frustration = Clamp01(_frustration + delta); break;
                case "satisfaction": _satisfaction = Clamp01(_satisfaction + delta); break;
                case "social": _socialDrive = Clamp01(_socialDrive + delta); break;
                case "cognitive_load": _cognitiveLoad = Clamp01(_cognitiveLoad + delta); break;
                default: throw new ArgumentException($"Unknown subsystem \"{subsystem}\".", nameof(subsystem));
            }
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Sets the activity level (REST/LOW/NORMAL/FOCUSED/INTENSE — mind §8).
    /// Unknown values fail closed to REST (mind §8: the idle is a valid result).</summary>
    public void SetActivityLevel(string? level)
    {
        lock (_gate)
        {
            _activityLevel = level?.Trim().ToUpperInvariant() switch
            {
                "REST" or "LOW" or "NORMAL" or "FOCUSED" or "INTENSE" => level!.Trim().ToUpperInvariant(),
                _ => "REST"
            };
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Sets the attention target (routing only — NEVER an authorization input).</summary>
    public void SetAttention(string? target)
    {
        lock (_gate)
        {
            _attentionTarget = string.IsNullOrWhiteSpace(target) ? null : target.Trim();
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Sets the explicit uncertainty note (mind §5: personality never hides it).</summary>
    public void SetUncertainty(string? note)
    {
        lock (_gate)
        {
            _uncertaintyNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);
}

/// <summary>Immutable point-in-time snapshot (the persistence contract, versioned).</summary>
public sealed record MindSnapshot(
    int Version,
    double Boredom,
    double Curiosity,
    double Novelty,
    double Frustration,
    double Satisfaction,
    double SocialDrive,
    double CognitiveLoad,
    string ActivityLevel,
    string? AttentionTarget,
    string? UncertaintyNote,
    DateTimeOffset UpdatedAt);
