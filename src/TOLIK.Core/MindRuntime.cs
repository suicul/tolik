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

    // MI-3 (PRG-1c): the activity selection machinery — the drives → the fixed
    // candidate catalog → utility → ONE activity, «просто idle — тоже правильный
    // результат». The security state is a real input (LOCKED restricts the catalog).
    private readonly ActivitySelector _activities = new();
    private bool _locked;
    private int _selectorTickCount;
    private string? _currentActivity;

    // MI-4 (PRG-1d): the internal goals (the occupations as bounded trackable objects)
    // and the explicit uncertainty level (the real signals: failures raise it, the
    // steady observations decay it — never a guess).
    private readonly GoalStore _goals = new();
    private double _uncertainty;

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

    /// <summary>The Core's LOCKED state — a real input to the mind (the Safety drive
    /// spikes; the activity catalog restricts to the safety-relevant candidates —
    /// the mind does not wander while locked). Fed by the host watcher each tick.</summary>
    public void MarkSecurityState(bool locked)
    {
        lock (_gate) { _locked = locked; }
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
            // MI-4: a failure raises the explicit uncertainty — something went
            // unexpectedly (a real observation, never a guess; decays in the tick).
            _uncertainty = Math.Clamp(_uncertainty + 0.20, 0.0, 1.0);
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

    /// <summary>CA-2 (PRG-3a): a running task went STALLED — the supervisor's honest
    /// observation. A stall is mildly frustrating and raises the uncertainty
    /// (something is not progressing as expected — a real signal, never a guess).</summary>
    public void OnTaskStalled()
    {
        lock (_gate)
        {
            _state.Adjust("frustration", +0.05);
            _uncertainty = Math.Clamp(_uncertainty + 0.10, 0.0, 1.0);
            _journal.Record(MindJournalKind.TaskStalled);
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

            // MI-4: the explicit uncertainty decays as the observations stabilize
            // (~0.5%/tick — a real signal: the failures raise it, time calms it).
            _uncertainty = Math.Clamp(_uncertainty * 0.995, 0.0, 1.0);

            // MI-4: the goals tick — the REAL satisfaction/expiry rules; the journal
            // records the status TRANSITIONS only (the steady state is not noise).
            foreach (var (goal, newStatus) in _goals.Tick(_activeTasks, _curiosity.Intensity, now))
                _journal.RecordGoalOutcome(goal.Type, newStatus, now);

            // MI-4: the attention arbitration (mind §5): the direct interaction outranks
            // the active task, which outranks the internal occupation; idle is valid.
            // Routing state ONLY — NEVER an authorization input.
            _state.SetAttention(
                minutesSince < 2.0 ? "user" :
                _activeTasks > 0 ? "task" :
                _currentActivity ?? "idle");

            // MI-3 (PRG-1c): the activity selection every ~30 ticks (30 s at 1 Hz) —
            // drives → the fixed candidate catalog → utility → ONE activity («просто
            // idle — тоже правильный результат», mind §8). The journal records the
            // TRANSITIONS only (a steady activity is not noise); LOCKED restricts the
            // catalog to the safety-relevant candidates (the mind does not wander).
            _selectorTickCount++;
            if (_selectorTickCount >= 30)
            {
                _selectorTickCount = 0;
                var drives = DriveSystem.Compute(_state.Snapshot(), new MindInputs(minutesSince, _activeTasks, _locked));
                var selection = _activities.Select(drives, now, _locked);
                if (!string.Equals(_currentActivity, selection.Activity, StringComparison.Ordinal))
                {
                    _currentActivity = selection.Activity;
                    _journal.RecordActivity(selection.Summary, now);
                    // MI-4: the non-idle occupation becomes/refreshes an internal goal
                    // (bounded, deduped per type — the occupations, not the noise).
                    if (selection.Activity != ActivityNames.Idle)
                        _goals.UpsertFromActivity(selection.Activity, now);
                }
            }
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
                Attention: attention,
                Uncertainty: _uncertainty);
        }
    }

    /// <summary>
    /// The bounded reflection (mind.md §5, MI-4): the deterministic outcome statistics
    /// over the recent journal window — REAL observations only, never invented lessons.
    /// «Как прошло recent время?» reads from this.
    /// </summary>
    public string ReflectionSummary(TimeSpan window, DateTimeOffset now)
    {
        lock (_gate)
        {
            var from = now - window;
            var recent = _journal.Recent(512).Where(e => e.At >= from).ToList();
            var completed = recent.Count(e => e.Kind == MindJournalKind.TaskCompleted);
            var failed = recent.Count(e => e.Kind == MindJournalKind.TaskFailed);
            var satisfied = recent.Count(e => e.Kind == MindJournalKind.GoalOutcome && e.Summary.StartsWith("Цель достигнута", StringComparison.Ordinal));
            var total = completed + failed;
            return total == 0
                ? "Недавних исходов задач не было."
                : $"Из {total} исходов задач: завершено {completed}, провалено {failed}; целей достигнуто: {satisfied}.";
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
/// Cephalon expresses the REAL mind — curiosity/frustration/confidence/attention;
/// PRG-1d: uncertainty is wired too). Amusement/Thinking have no real source yet.</summary>
public sealed record MindEmotions(
    double Curiosity,
    double Frustration,
    double Confidence,
    double Attention,
    double Uncertainty = 0.0);
