namespace TOLIK.Core;

/// <summary>The fixed candidate-activity catalog (docs/mind.md §3 — bounded literals,
/// never user content). v1 selection machinery: the non-idle candidates are selected
/// and journaled; their EXECUTION wires up with MI-4/SE-5 (honest boundary).</summary>
public static class ActivityNames
{
    public const string Idle = "Idle";
    public const string SystemReview = "SystemReview";
    public const string TaskFollowUp = "TaskFollowUp";
    public const string Investigation = "Investigation";
    public const string KnowledgeTidy = "KnowledgeTidy";
}

/// <summary>One candidate: its fixed name + its drive affinities (the utility weights).</summary>
public sealed record ActivityCandidate(string Name, IReadOnlyList<string> DriveAffinities, string Summary)
{
    public static ActivityCandidate Idle { get; } = new(ActivityNames.Idle, [DriveNames.Rest], "Покой");
    public static ActivityCandidate SystemReview { get; } = new(ActivityNames.SystemReview, [DriveNames.Maintenance, DriveNames.Safety, DriveNames.Order], "Наблюдение за системой");
    public static ActivityCandidate TaskFollowUp { get; } = new(ActivityNames.TaskFollowUp, [DriveNames.Completion, DriveNames.ProblemSolving], "Возврат к задачам");
    public static ActivityCandidate Investigation { get; } = new(ActivityNames.Investigation, [DriveNames.Curiosity, DriveNames.Novelty, DriveNames.Exploration], "Исследование");
    public static ActivityCandidate KnowledgeTidy { get; } = new(ActivityNames.KnowledgeTidy, [DriveNames.Order, DriveNames.Learning], "Разбор накопленного");

    public static IReadOnlyList<ActivityCandidate> Catalog { get; } = [Idle, SystemReview, TaskFollowUp, Investigation, KnowledgeTidy];
}

/// <summary>The selection result: the chosen activity, its utility, the drives it read.</summary>
public sealed record ActivitySelection(string Activity, string Summary, double Utility)
{
    public bool IsValid => double.IsFinite(Utility) && Utility >= 0;
}

/// <summary>
/// ActivitySelector (docs/mind.md §3/§4, MI-3 — PLAN REFORM PRG-1c): drives → the
/// fixed candidate catalog → utility → ONE selected activity. Deterministic: score =
/// Σ(drive affinity × drive intensity) × the cooldown factor, plus the Idle baseline
/// («просто idle — тоже правильный результат» — mind §8); ties resolve to Idle (the
/// conservative result). The per-candidate cooldown is the anti-loop (the same
/// candidate is suppressed for N minutes after selection); LOCKED restricts the
/// catalog to the safety-relevant candidates only (Idle/SystemReview — the mind
/// does not wander while the Core is locked). The drives create MOTIVATION, never
/// permission — the selected activity is journaled and becomes the attention target;
/// every execution still passes the authorization pipeline (governance).
/// </summary>
public sealed class ActivitySelector
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSelected = new(StringComparer.Ordinal);

    /// <summary>The anti-loop cooldown (TOLIK_MIND_ACTIVITY_COOLDOWN_MINUTES; default 10; fail closed).</summary>
    public TimeSpan Cooldown { get; } =
        double.TryParse(Environment.GetEnvironmentVariable("TOLIK_MIND_ACTIVITY_COOLDOWN_MINUTES"), out var m) && double.IsFinite(m) && m > 0
            ? TimeSpan.FromMinutes(m)
            : TimeSpan.FromMinutes(10);

    /// <summary>Selects ONE activity for the current drives. Deterministic given (drives, now, locked).
    /// LOCKED restricts to the safety-relevant candidates; idleOnly (the Emergency
    /// Stop, Phase G) restricts to Idle alone — the stopped mind simply rests.</summary>
    public ActivitySelection Select(IReadOnlyList<DriveState> drives, DateTimeOffset now, bool locked, bool idleOnly = false)
    {
        ArgumentNullException.ThrowIfNull(drives);
        lock (_gate)
        {
            var byName = drives.GroupBy(d => d.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Intensity, StringComparer.Ordinal);

            ActivitySelection best = new(ActivityNames.Idle, ActivityCandidate.Idle.Summary, IdleBaselineUtility(byName));
            var bestActivity = ActivityNames.Idle;
            foreach (var candidate in ActivityCandidate.Catalog)
            {
                if (idleOnly && candidate.Name != ActivityNames.Idle)
                    continue; // Emergency Stop: the autonomy pauses — the mind rests
                if (locked && candidate.Name is not (ActivityNames.Idle or ActivityNames.SystemReview))
                    continue; // LOCKED: only the safety-relevant candidates
                if (candidate.Name != ActivityNames.Idle && IsCoolingDown(candidate.Name, now))
                    continue; // the anti-loop: the recently-selected candidate rests

                var utility = candidate.Name == ActivityNames.Idle
                    ? IdleBaselineUtility(byName)
                    : candidate.DriveAffinities.Sum(a => byName.GetValueOrDefault(a, 0.0));
                if (!double.IsFinite(utility)) continue;
                if (utility > best.Utility)
                {
                    best = new ActivitySelection(candidate.Name, candidate.Summary, utility);
                    bestActivity = candidate.Name;
                }
            }

            if (bestActivity != ActivityNames.Idle || _lastSelected.Count > 0)
                _lastSelected[bestActivity] = now; // Idle resets its own cooldown implicitly (always available)
            return best;
        }
    }

    private static double IdleBaselineUtility(IReadOnlyDictionary<string, double> byName) =>
        Math.Max(0.15, byName.GetValueOrDefault(DriveNames.Rest, 0.0)); // idle is always a plausible result

    /// <summary>The anti-loop cooldown check — READ-ONLY for the Inspector (never
    /// mutates; the real selection's cooldowns are untouched by a trace).</summary>
    internal bool IsCoolingDown(string activity, DateTimeOffset now) =>
        _lastSelected.TryGetValue(activity, out var at) && now - at < Cooldown;
}
