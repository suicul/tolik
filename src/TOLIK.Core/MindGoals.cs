namespace TOLIK.Core;

/// <summary>
/// MindGoals (docs/mind.md §5, MI-4 — PLAN REFORM PRG-1d): the mind's INTERNAL goals —
/// the selected occupations formalized as bounded, trackable objects. Deterministic
/// generation: a non-idle ActivitySelection creates/refreshes ONE active goal per
/// type (never one per tick); the satisfaction rules are REAL observations only:
/// TaskFollowUp is satisfied when the active work reaches zero, Investigation when the
/// curiosity drive decays below its threshold, the rest expire by timeout. Max 8 active
/// (the oldest expires); bounded; thread-safe. The goal-driven TASK creation (the full
/// loop into the planner/authorization) wires up with SE-5 — v1 goals are the mind's
/// own state + the journal context, never an authority to act.
/// </summary>
public static class GoalTypes
{
    public const string Investigation = "Investigation";
    public const string TaskFollowUp = "TaskFollowUp";
    public const string SystemReview = "SystemReview";
    public const string KnowledgeTidy = "KnowledgeTidy";
}

public sealed record MindGoal(
    string Type,
    string Source,             // the fixed activity name that created it (never user content)
    DateTimeOffset CreatedAt,
    string Status,             // Active | Satisfied | Expired (fixed literals)
    double Progress)           // [0..1] — the deterministic progress rule per type
{
    public bool IsValid => double.IsFinite(Progress) && Progress is >= 0.0 and <= 1.0;
}

public sealed class GoalStore
{
    public const int MaxActive = 8;
    private static readonly TimeSpan GoalExpiry = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly List<MindGoal> _goals = new();

    /// <summary>A non-idle activity selection creates/refreshes ONE active goal of that
    /// type (dedup: an existing active goal of the type just refreshes its CreatedAt).</summary>
    public void UpsertFromActivity(string activity, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(activity)) throw new ArgumentException("Activity is required.", nameof(activity));
        lock (_gate)
        {
            var existing = _goals.FirstOrDefault(g => g.Type == activity && g.Status == "Active");
            if (existing is not null)
            {
                _goals.Remove(existing);
                _goals.Add(existing with { CreatedAt = now }); // refreshed: the occupation continues
                return;
            }
            _goals.Add(new MindGoal(activity, activity, now, "Active", 0.0));
            ExpireOverflowLocked(now);
        }
    }

    /// <summary>The deterministic per-type progress [0..1] — REAL observations only:
    /// TaskFollowUp: the remaining work (0 active tasks = done); Investigation: the
    /// curiosity decay (the drive resolving = the investigation completing); the rest:
    /// the elapsed time toward the expiry.</summary>
    public double ProgressOf(string type, int activeTasks, double curiosity, DateTimeOffset now)
    {
        lock (_gate)
        {
            var goal = _goals.FirstOrDefault(g => g.Type == type && g.Status == "Active");
            if (goal is null) return 0.0;
            return type switch
            {
                GoalTypes.TaskFollowUp => activeTasks == 0 ? 1.0 : 0.5, // done when the work drains
                GoalTypes.Investigation => Math.Clamp(1.0 - curiosity / 0.15, 0.0, 1.0), // curiosity < 0.15 = satisfied
                _ => Math.Clamp((now - goal.CreatedAt).TotalMinutes / GoalExpiry.TotalMinutes, 0.0, 1.0) // toward expiry
            };
        }
    }

    /// <summary>The tick: updates the active goals' progress + status by the REAL rules
    /// (the satisfaction conditions + the expiry); returns the goals that CHANGED status
    /// (so the caller can journal the transitions, never the steady state).</summary>
    public IReadOnlyList<(MindGoal Goal, string NewStatus)> Tick(int activeTasks, double curiosity, DateTimeOffset now)
    {
        lock (_gate)
        {
            var changed = new List<(MindGoal, string)>();
            for (var i = 0; i < _goals.Count; i++)
            {
                var goal = _goals[i];
                if (goal.Status != "Active") continue;
                var progress = ProgressOf(goal.Type, activeTasks, curiosity, now);
                string? newStatus = null;
                if (goal.Type == GoalTypes.TaskFollowUp && activeTasks == 0) newStatus = "Satisfied";
                else if (goal.Type == GoalTypes.Investigation && curiosity < 0.15) newStatus = "Satisfied";
                else if (now - goal.CreatedAt >= GoalExpiry) newStatus = "Expired";
                var updated = goal with { Progress = progress };
                if (newStatus is not null)
                {
                    updated = updated with { Status = newStatus };
                    changed.Add((goal, newStatus));
                }
                _goals[i] = updated;
            }
            // Bounded: keep the history trimmed (the newest 32 records total).
            if (_goals.Count > 32)
                _goals.RemoveRange(0, _goals.Count - 32);
            return changed;
        }
    }

    public IReadOnlyList<MindGoal> Active()
    {
        lock (_gate)
        {
            return _goals.Where(g => g.Status == "Active").ToList();
        }
    }

    public IReadOnlyList<MindGoal> Recent(int limit = 16)
    {
        if (limit is < 1 or > 32) limit = 16;
        lock (_gate)
        {
            var all = _goals.ToList();
            return all.Count <= limit ? all : all.Skip(all.Count - limit).ToList();
        }
    }

    private void ExpireOverflowLocked(DateTimeOffset now)
    {
        var active = _goals.Where(g => g.Status == "Active").ToList();
        while (active.Count > MaxActive)
        {
            var oldest = active.OrderBy(g => g.CreatedAt).First();
            _goals.Remove(oldest);
            _goals.Add(oldest with { Status = "Expired", Progress = Math.Clamp((now - oldest.CreatedAt).TotalMinutes / GoalExpiry.TotalMinutes, 0.0, 1.0) });
            active.Remove(oldest);
        }
    }
}
