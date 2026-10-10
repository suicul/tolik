namespace TOLIK.Infrastructure.Authorization;

/// <summary>
/// Autonomy level spectrum (docs/realistic-autonomy.md §1, RA-0): L0 Reactive →
/// L5 Long-Term Experimental — a SPECTRUM, never a boolean. The level CAPS the
/// autonomy decisions (L0: even safe autonomous acts become Confirm; L3+: the full
/// band). Higher autonomy NEVER means unrestricted privilege — the authorization
/// policy always outranks the level (fail-closed: locked/guest deny regardless).
/// Configurable via TOLIK_AUTONOMY_LEVEL (default L2 = Supervised Autonomous).
/// </summary>
public enum AutonomyLevel
{
    /// <summary>L0: responds to direct requests only — every autonomous decision becomes Confirm.</summary>
    Reactive = 0,
    /// <summary>L1: multi-step actions after a request; autonomous decisions become Notify (never silent).</summary>
    Assisted = 1,
    /// <summary>L2 (default): continues an EXPLICITLY authorized task while the owner is absent (lease/scope bound).</summary>
    SupervisedAutonomous = 2,
    /// <summary>L3: selects subtasks, recovers, maintains, approved background — within strict policy + budgets.</summary>
    BoundedAutonomous = 3,
    /// <summary>L4: generates useful goals from observations/state — predefined domains + budgets only.</summary>
    Proactive = 4,
    /// <summary>L5: independent learning/self-improvement experiments in ISOLATED environments — never unrestricted host authority.</summary>
    LongTermExperimental = 5
}

public static class AutonomyLevelPolicy
{
    /// <summary>Configured level (TOLIK_AUTONOMY_LEVEL; default L2 — docs/realistic-autonomy.md §1).</summary>
    public static AutonomyLevel Configured =>
        Enum.TryParse<AutonomyLevel>(Environment.GetEnvironmentVariable("TOLIK_AUTONOMY_LEVEL")?.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : AutonomyLevel.SupervisedAutonomous;

    /// <summary>
    /// Caps an autonomy decision by the configured level: L0 Reactive demotes even
    /// Autonomous to Confirm (nothing acts without asking); L1 Assisted demotes
    /// Autonomous to Notify (acts, but reports); L2+ leaves the decision as-is.
    /// Deny stays Deny at every level (authorization outranks the level). Deterministic.
    /// </summary>
    public static AutonomyDecision Cap(AutonomyDecision decision, AutonomyLevel? level = null)
    {
        var effective = level ?? Configured;
        if (decision == AutonomyDecision.Deny) return decision; // authorization outranks
        return effective switch
        {
            AutonomyLevel.Reactive => AutonomyDecision.Confirm,
            AutonomyLevel.Assisted when decision == AutonomyDecision.Autonomous => AutonomyDecision.Notify,
            _ => decision
        };
    }
}

/// <summary>
/// Consent model (docs/realistic-autonomy.md §4, RA-2): a structured, scoped, TARGET-BOUND
/// and REVOCABLE grant — never a binary "UserAllowed = true". Natural-language consent
/// («Продолжай», «Делай пока не закончишь», «Пока меня нет — занимайся этим») parses into
/// bounded grants via ParseGrant; «Толик, прекращай» → Revocation (stop + preserve + report).
/// The consent extends the existing ConfirmationStore/leases — no parallel systems; the
/// authorization policy remains the final gate. Deterministic.
/// </summary>
public sealed record ConsentGrant(
    string Action,
    string Target,
    ConsentScopeKind Scope,
    DateTimeOffset GrantedAt,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Conditions,
    string? Evidence)
{
    /// <summary>Target-bound check: the grant covers THIS target only (§4/§15: «почини
    /// голосовой сервис» ≠ «modify anything»). A null target on the grant = any target.</summary>
    public bool Covers(string target) => string.IsNullOrWhiteSpace(Target) ||
        string.Equals(Target, target, StringComparison.OrdinalIgnoreCase);
}

public enum ConsentScopeKind
{
    /// <summary>«Сделай сейчас» — one action, consumed after use.</summary>
    OneTime,
    /// <summary>«Доведи проект» — until the task completes (stop conditions bound it).</summary>
    TaskScoped,
    /// <summary>«Управляй этим приложением в этой сессии» — until the session ends.</summary>
    SessionScoped,
    /// <summary>«Можешь maintain этот сервис» — a named service/domain, long-lived.</summary>
    DomainScoped,
    /// <summary>Persistent LOW-RISK maintenance only (never high-risk — §4/§13).</summary>
    PersistentLowRisk
}

public static class ConsentModel
{
    private static readonly object Gate = new();
    private static readonly List<ConsentGrant> Active = new();

    /// <summary>Records a grant (scoped; revocable). Returns the stored grant.</summary>
    public static ConsentGrant Grant(string action, string target, ConsentScopeKind scope,
        TimeSpan? duration = null, IReadOnlyList<string>? conditions = null, string? evidence = null)
    {
        if (string.IsNullOrWhiteSpace(action)) throw new ArgumentException("Action is required.", nameof(action));
        var grant = new ConsentGrant(action.Trim(), target?.Trim() ?? string.Empty, scope,
            DateTimeOffset.UtcNow, duration is { } d ? DateTimeOffset.UtcNow + d : null,
            conditions ?? [], evidence);
        lock (Gate) { Active.Add(grant); }
        return grant;
    }

    /// <summary>Checks an active, unexpired, target-bound grant for the action.</summary>
    public static ConsentGrant? FindActive(string action, string? target, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        lock (Gate)
        {
            return Active.LastOrDefault(g =>
                string.Equals(g.Action, action, StringComparison.OrdinalIgnoreCase) &&
                g.Covers(target ?? string.Empty) &&
                (g.ExpiresAt is null || g.ExpiresAt > at));
        }
    }

    /// <summary>Revokes ALL grants for an action («Толик, прекращай») — stop + preserve. True when any revoked.</summary>
    public static bool RevokeAll(string action)
    {
        lock (Gate)
        {
            var removed = Active.RemoveAll(g => string.Equals(g.Action, action, StringComparison.OrdinalIgnoreCase));
            return removed > 0;
        }
    }

    /// <summary>
    /// Natural-language consent parsing (docs/realistic-autonomy.md §4/§14): the bounded
    /// phrases → the structured grants. Deterministic; the fallback = OneTime («сделай
    /// сейчас») — never a broad permission from an ambiguous phrase.
    /// </summary>
    public static (ConsentScopeKind Scope, TimeSpan? Duration) ParseGrantPhrase(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return (ConsentScopeKind.OneTime, null);
        var lower = phrase.ToLowerInvariant();
        if (lower.Contains("пока не закончишь") || lower.Contains("до конца") || lower.Contains("доведи"))
            return (ConsentScopeKind.TaskScoped, null);
        if (lower.Contains("пока меня нет") || lower.Contains("пока меня не будет") || lower.Contains("я ушёл") || lower.Contains("я ушел"))
            return (ConsentScopeKind.TaskScoped, TimeSpan.FromHours(8)); // the absence lease: bounded 8h
        if (lower.Contains("в этой сессии"))
            return (ConsentScopeKind.SessionScoped, null);
        if (lower.Contains("всегда") || lower.Contains("постоянно"))
            return (ConsentScopeKind.PersistentLowRisk, null); // low-risk only, NEVER high-risk
        return (ConsentScopeKind.OneTime, null); // fallback: never broad from ambiguous
    }
}
