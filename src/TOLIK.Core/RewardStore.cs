namespace TOLIK.Core;

/// <summary>
/// The reward types (docs/realistic-autonomy.md RA-3): ACHIEVEMENT = a real RESULT
/// (the task completed, the problem solved); EFFORT_ACKNOWLEDGEMENT = the honest
/// effort WITHOUT the result (the task failed but the work was real — the separate
/// type, never a fake achievement); CELEBRATION = the shared moment with the owner;
/// SOCIAL = the social interaction reward. The Cosmetic/Creative/SharedExperience
/// types land with their tracks — additive.
/// </summary>
public enum RewardType
{
    Achievement,
    EffortAcknowledgement,
    Celebration,
    Social
}

/// <summary>One earned reward: WHEN, WHAT type, the deterministic summary, and WHO
/// awarded it (the provenance is MANDATORY — TOLIK never grants itself a reward
/// without a real event source; § RA-3 anti-reward-hacking).</summary>
public sealed record RewardEvent(
    DateTimeOffset At,
    RewardType Type,
    string Summary,
    string Source);

/// <summary>
/// RewardStore (docs/realistic-autonomy.md RA-3 — PLAN REFORM PRG-5a): the memory of
/// earned rewards — a bounded ring of REAL events. **ANTI-REWARD-HACKING IS
/// STRUCTURAL, not a filter**: (1) the Source provenance is MANDATORY — a reward
/// without a real event source cannot be recorded; (2) the store's API carries NO
/// authority — a reward never touches permissions, identity, or the authorization
/// pipeline (structurally tested: the type has no such surface); (3) the rewards
/// INFORM the personality/relationship layer — they never alter any decision.
/// Effort-vs-Result: the failed work can earn the EffortAcknowledgement — honestly
/// typed as the effort, never dressed up as the achievement. Bounded ring;
/// thread-safe.
/// </summary>
public sealed class RewardStore
{
    public const int Capacity = 256;

    private readonly object _gate = new();
    private readonly Queue<RewardEvent> _rewards = new();

    /// <summary>Records one earned reward. The SOURCE is mandatory (who/what awarded:
    /// "owner", "system:task_completed", "event:voice_turn" — the real provenance);
    /// the summary is the caller's deterministic rendering of the real outcome.</summary>
    public void Record(RewardType type, string summary, string source, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(summary)) throw new ArgumentException("Summary is required (the deterministic rendering of the real outcome).", nameof(summary));
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Source (provenance) is REQUIRED — TOLIK never grants itself a reward without a real event source (anti-reward-hacking).", nameof(source));
        lock (_gate)
        {
            _rewards.Enqueue(new RewardEvent(at ?? DateTimeOffset.UtcNow, type, summary.Trim(), source.Trim()));
            while (_rewards.Count > Capacity) _rewards.Dequeue();
        }
    }

    public IReadOnlyList<RewardEvent> Recent(int limit = 16)
    {
        if (limit is < 1 or > Capacity) limit = 16;
        lock (_gate)
        {
            var all = _rewards.ToArray();
            return all.Length <= limit ? all : all.Skip(all.Length - limit).ToArray();
        }
    }

    /// <summary>The honest statistics: the achievements vs the effort
    /// acknowledgements — «3 из 5 недавних наград — за результат, 2 — за старание».
    /// Never dressed up: the counts are the real counts.</summary>
    public (int Achievements, int EffortAcknowledgements) EffortVsResult(TimeSpan window, DateTimeOffset now)
    {
        lock (_gate)
        {
            var from = now - window;
            var recent = _rewards.Where(r => r.At >= from).ToList();
            return (recent.Count(r => r.Type == RewardType.Achievement),
                    recent.Count(r => r.Type == RewardType.EffortAcknowledgement));
        }
    }
}
