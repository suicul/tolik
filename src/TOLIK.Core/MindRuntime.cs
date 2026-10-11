namespace TOLIK.Core;

/// <summary>
/// MindRuntime (docs/mind.md MI-0..2 + PLAN REFORM PRG-1a): TOLIK's ONE running mind —
/// the deterministic wiring of the MI-0 MindState to the MI-2 drives (BoredomModel /
/// NoveltyTracker / CuriosityModel) from REAL measurable inputs only: the last
/// meaningful interaction, the active task count, the task outcomes. ONE instance
/// per TOLIK (the identity is a single entity). The mind creates MOTIVATION, never
/// permission — every action still passes the authorization pipeline (governance);
/// nothing here grants or escalates anything. Thread-safe, bounded, no hot-path
/// allocations; nothing fabricated (§50): unmapped signals stay honest zeros.
/// </summary>
public sealed class MindRuntime
{
    private readonly object _gate = new();
    private readonly MindState _state = new();
    private readonly BoredomModel _boredomModel = new();
    private readonly NoveltyTracker _novelty = new();
    private readonly CuriosityModel _curiosity = new();

    private DateTimeOffset _lastInteraction = DateTimeOffset.MinValue;
    private int _activeTasks;

    // PRG-1b (MI-5 + MI-6): the operational journal (privacy by construction — no user
    // content ever enters it) and the transition-based emotion engine. Both thread-safe
    // internally; the feeds happen under this runtime's gate (no reverse lock order).
    private readonly MindJournal _journal = new();
    private readonly EmotionEngine _emotions = new();

    /// <summary>TOLIK's operational journal («Чем занимался пока меня не было?» — mind.md §7).
    /// The entries contain ONLY fixed kinds + generic summaries: safe for any authenticated reader.</summary>
    public MindJournal Journal => _journal;

    /// <summary>The slow mood layer (MI-5): valence/arousal/momentum from the real transitions.</summary>
    public MoodState MoodSnapshot() => _emotions.Mood;

    /// <summary>Point-in-time mind snapshot (the versioned MI-0 record).</summary>
    public MindSnapshot Snapshot() { lock (_gate) { return _state.Snapshot(); } }

    /// <summary>A meaningful user interaction (a chat message, a voice turn): resets
    /// the inactivity clock and bumps the social drive — real input, never invented.</summary>
    public void MarkInteraction(DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            _lastInteraction = at ?? DateTimeOffset.UtcNow;
            _state.Adjust("social", +0.10);
            _journal.Record(MindJournalKind.Interaction, at);
        }
    }

    /// <summary>Task lifecycle events (from the host's TaskLifecycle.EventPublished).</summary>
    public void OnTaskStarted()
    {
        lock (_gate)
        {
            _activeTasks++;
            _state.Adjust("cognitive_load", +0.10);
            _curiosity.Register("new_application");
            SyncDrives();
            _journal.Record(MindJournalKind.TaskStarted);
        }
    }

    public void OnTaskCompleted()
    {
        lock (_gate)
        {
            _activeTasks = Math.Max(0, _activeTasks - 1);
            _state.Adjust("satisfaction", +0.05); // a real success — cautious growth
            _novelty.Register("repeat");          // routine outcome: novelty decays
            _curiosity.Register("satisfied");     // the drive's object is resolved
            SyncDrives();
            _journal.Record(MindJournalKind.TaskCompleted);
        }
    }

    public void OnTaskFailed()
    {
        lock (_gate)
        {
            _activeTasks = Math.Max(0, _activeTasks - 1);
            _state.Adjust("frustration", +0.10);
            _curiosity.Register("failed_action");
            _novelty.Register("unexpected");
            SyncDrives();
            _journal.Record(MindJournalKind.TaskFailed);
        }
    }

    public void OnTaskCancelled()
    {
        lock (_gate)
        {
            _activeTasks = Math.Max(0, _activeTasks - 1);
            _journal.Record(MindJournalKind.TaskCancelled);
        }
    }

    /// <summary>
    /// The 1 Hz tick (driven by the host's MindRuntimeService): the boredom emerges
    /// from the measurable conditions via the existing MI-2 BoredomModel (never a
    /// timer); the drive levels land in the MindState; the short-term reactions decay
    /// toward the baseline (mind §10: surprise passes quickly, the mood lingers);
    /// the activity level follows the real conditions (mind §8: idle is a valid result).
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        lock (_gate)
        {
            // MI-5: the emotion engine measures the PREVIOUS→current transition BEFORE
            // this tick's state updates (the events' effects are the transitions).
            _emotions.OnSnapshot(_state.Snapshot());
            var minutesSince = MinutesSinceInteraction(now);
            var recent = minutesSince < 5.0;
            var boredom = _boredomModel.Compute(minutesSince, _activeTasks > 0, _novelty.Current, recent);
            _state.Adjust("boredom", boredom - _state.Boredom); // set toward the computed level
            _state.Adjust("curiosity", _curiosity.Intensity - _state.Curiosity);
            _state.Adjust("novelty", _novelty.Current - _state.Novelty);
            // Short-term reaction decay (~1%/tick → e-fold ≈ 100 s at 1 Hz).
            _state.Adjust("frustration", -_state.Frustration * 0.01);
            _state.Adjust("satisfaction", -_state.Satisfaction * 0.01);
            _state.Adjust("social", -_state.SocialDrive * 0.005);
            _state.SetActivityLevel(_activeTasks > 0 ? "FOCUSED" : boredom >= 0.6 ? "REST" : "LOW");
        }
    }

    /// <summary>
    /// The honest mind→expression mapping (read by the CephalonExpressionService):
    /// every value traces to a real mind drive. Amusement/Uncertainty/Thinking have
    /// no real source yet (humor/planner wiring land with their tracks) — they stay
    /// honest zeros there, never fabricated here. Boredom/Novelty have no
    /// ExpressionState field yet — mapped when the contract grows, never faked onto
    /// the wrong field.
    /// </summary>
    public MindEmotions ExpressionEmotions(DateTimeOffset now)
    {
        lock (_gate)
        {
            var minutesSince = MinutesSinceInteraction(now);
            // Attention: the interaction focus decays over ~10 minutes to honest 0.
            var attention = minutesSince >= 10.0 ? 0.0 : 1.0 - minutesSince / 10.0;
            return new MindEmotions(
                Curiosity: _state.Curiosity,
                Frustration: _state.Frustration,
                Confidence: _state.Satisfaction, // satisfaction ≈ earned confidence
                Attention: attention);
        }
    }

    /// <summary>Syncs the drive models' current levels into the MindState (the
    /// canonical state the expression reads) — called by the events and the tick.</summary>
    private void SyncDrives()
    {
        _state.Adjust("curiosity", _curiosity.Intensity - _state.Curiosity);
        _state.Adjust("novelty", _novelty.Current - _state.Novelty);
    }

    private double MinutesSinceInteraction(DateTimeOffset now) =>
        _lastInteraction == DateTimeOffset.MinValue
            ? 10_000.0 // never interacted: fully saturated inactivity
            : Math.Max(0.0, (now - _lastInteraction).TotalMinutes);
}

/// <summary>The mind-driven emotional values for the ExpressionState (PRG-1a: the
/// Cephalon expresses the REAL mind — curiosity/frustration/confidence/attention).</summary>
public sealed record MindEmotions(
    double Curiosity,
    double Frustration,
    double Confidence,
    double Attention);
