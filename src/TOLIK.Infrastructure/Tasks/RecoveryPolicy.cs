namespace TOLIK.Infrastructure.Tasks;

/// <summary>
/// Bounded recovery policy (docs/continuous-autonomy.md CA-6/§7, CA-2): a failed action
/// is classified, retried ONCE as-is, then an alternate strategy, then escalation —
/// НИКОГДА build→fail→build→fail→forever. Deterministic, no model involvement; the
/// escalation («pause + ask owner») is the terminal outcome — the caller decides.
/// </summary>
public static class RecoveryPolicy
{
    public const int MaxSameActionRetries = 1;
    public const int MaxTotalAttempts = 3;

    /// <summary>Recovery verdicts for a failed attempt.</summary>
    public enum RecoveryAction
    {
        /// <summary>Retry the SAME action once (transient failures: network hiccup, timeout).</summary>
        RetrySame,
        /// <summary>Try an ALTERNATE strategy (the same-action retry already failed).</summary>
        AlternateStrategy,
        /// <summary>STOP: pause the task, preserve the state, ask the owner — the budget is spent.</summary>
        EscalatePauseAndAsk
    }

    /// <summary>
    /// Decides the recovery for a failed attempt: attempt 0 (first failure) → RetrySame;
    /// attempt 1 → AlternateStrategy; attempt ≥ MaxTotalAttempts−1 → EscalatePauseAndAsk.
    /// Deterministic; the counters come from the task's evidence (journal/FailedAttempts).
    /// </summary>
    public static RecoveryAction Decide(int failedAttempts) =>
        failedAttempts switch
        {
            < 1 => RecoveryAction.RetrySame,
            < MaxTotalAttempts - 1 => RecoveryAction.AlternateStrategy,
            _ => RecoveryAction.EscalatePauseAndAsk
        };

    /// <summary>Classifies a failure from its safe summary (CA-2/§19): transient (retry
    /// makes sense) vs permanent (escalate immediately — a retry wastes budget).</summary>
    public static FailureKind ClassifyFailure(string? failureSummary)
    {
        if (string.IsNullOrWhiteSpace(failureSummary)) return FailureKind.Unknown;
        var lower = failureSummary.ToLowerInvariant();
        foreach (var marker in new[] { "timeout", "network", "connection", "unavailable", "temporarily", "hiccup" })
            if (lower.Contains(marker, StringComparison.Ordinal)) return FailureKind.Transient;
        foreach (var marker in new[] { "compile", "syntax", "assert", "invalid", "notfound", "denied", "missing" })
            if (lower.Contains(marker, StringComparison.Ordinal)) return FailureKind.Permanent;
        return FailureKind.Unknown;
    }
}

public enum FailureKind
{
    /// <summary>Transient (network/timeout/unavailable): a retry makes sense.</summary>
    Transient,
    /// <summary>Permanent (compile/syntax/assert): escalate, retries waste budget.</summary>
    Permanent,
    /// <summary>Unclassified: treat conservatively (the bounded retry policy applies).</summary>
    Unknown
}

/// <summary>
/// WaitUntil contract (docs/continuous-autonomy.md CA-6/§15, temporal reasoning):
/// semantic waits instead of arbitrary sleeps. A condition + timeout + poll interval;
/// the outcome distinguishes met/timeout — the caller recovers on timeout (never a
/// bare Thread.Sleep for a state transition). Deterministic; the condition is a
/// delegate (deterministic state check or visual check — the provider-agnostic).
/// </summary>
public sealed record WaitUntilSpec(string Description, TimeSpan Timeout, TimeSpan PollInterval);

public static class WaitUntil
{
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Waits until the condition is met or the timeout expires. Returns true when met.
    /// NO arbitrary sleeps: the poll interval bounds the CPU; the timeout bounds the wait.
    /// The cancellation is honoured immediately.
    /// </summary>
    public static async Task<bool> ConditionAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var poll = pollInterval ?? DefaultPoll;
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (condition()) return true;
            if (DateTimeOffset.UtcNow >= deadline) return false;
            try { await Task.Delay(poll, cancellationToken); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }
}
