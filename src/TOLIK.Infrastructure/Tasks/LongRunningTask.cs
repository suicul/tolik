namespace TOLIK.Infrastructure.Tasks;

/// <summary>
/// The EXPLICIT bounded scope for broad instructions (docs/continuous-autonomy.md CA-0/§4):
/// a wide objective («доведи проект до конца») is executed ONLY inside this scope.
/// NO unlimited authority — «do what is necessary» never silently expands: the
/// forbidden-operations denylist plus the stop conditions keep the authorization
/// boundaries authoritative (deleting unrelated files, firewall changes, random software,
/// exposing services, messages, money, security — outside scope → deny). File/network
/// access stays with the EXISTING AgentPathGuard enforcement; <c>AllowedRoots</c> mirror
/// TOLIK_ALLOWED_ROOTS semantics declaratively (the guard remains authoritative — this
/// record never bypasses it).
/// </summary>
public sealed record TaskScope(
    string Project,
    string Objective,
    IReadOnlyList<string> AllowedCapabilities,
    IReadOnlyList<string> AllowedRoots,
    TimeSpan? MaxDuration,
    TimeSpan? TimeBudget,
    IReadOnlyList<string> ForbiddenOperations,
    IReadOnlyList<string> StopConditions,
    string CompletionCriteria);

/// <summary>
/// One structured task-journal entry (CA-4/§9 — the mind.md journal pattern):
/// timestamp/event/action/result/reason/next step. NO transcripts, NO secrets —
/// safe summaries only (the journal feeds natural conversation later, «что ты делал?»).
/// </summary>
public sealed record TaskJournalEntry(DateTimeOffset Timestamp, string Event, string? Action, string? Result, string? Reason, string? NextStep);

/// <summary>
/// The REAL progress snapshot (CA-4/§8): every field is derived from actual evidence
/// (journal outcomes + the lifecycle state), never fabricated (§50 no fake progress).
/// A running task NEVER reports 100 — remaining work is unobserved; 100 comes only from
/// the lifecycle's own Completed terminal state.
/// </summary>
public sealed record TaskProgressSnapshot(double OverallPercent, string Phase, int CompletedSteps, string? CurrentStep, int FailedAttempts, string? LastSuccessfulAction, string? CurrentHypothesis, double Confidence);

/// <summary>
/// Deterministic task-journal renderer (CA-4/§9): user-facing summaries in the plan's own
/// format — «14:21 — build failed … 14:44 — tests passed». Each line is rendered in the
/// entry's own UTC offset (no machine-timezone conversion — deterministic), chronologically
/// ordered (stable sort: equal timestamps keep input order); the result is appended with
/// «→» when present.
/// </summary>
public static class TaskJournal
{
    public static string Render(IReadOnlyList<TaskJournalEntry> entries)
    {
        if (entries is null || entries.Count == 0) return string.Empty;
        return string.Join(Environment.NewLine, entries
            .OrderBy(x => x.Timestamp)
            .Select(x =>
            {
                var text = x.Event;
                if (!string.IsNullOrWhiteSpace(x.Result)) text += $" → {x.Result}";
                return $"{x.Timestamp.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)} — {text}";
            }));
    }
}
