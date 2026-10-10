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
    /// <summary>Configured level (TOLIK_AUTONOMY_LEVEL; default L2 — docs/realistic-autonomy.md §1).
    /// Fail-closed (§14): an unset variable keeps the default; an INVALID value (unparseable
    /// or out-of-range — Enum.TryParse accepts "99") falls to the MOST RESTRICTIVE level
    /// (Reactive), never to the permissive default — an ambiguous permission state denies.</summary>
    public static AutonomyLevel Configured
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("TOLIK_AUTONOMY_LEVEL")?.Trim();
            if (string.IsNullOrWhiteSpace(raw)) return AutonomyLevel.SupervisedAutonomous;
            return Enum.TryParse<AutonomyLevel>(raw, ignoreCase: true, out var parsed) &&
                   Enum.IsDefined(parsed)
                ? parsed
                : AutonomyLevel.Reactive; // fail closed: ambiguous config → most restrictive
        }
    }

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
    string? Evidence,
    string GrantedBy,
    string? BoundSessionId = null)
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
    /// <summary>Bounded active-grant list (drop-oldest under pressure — a lost stale
    /// grant costs nothing; matches the MindEventBus/MetricRecorder pattern).</summary>
    public const int MaxActiveGrants = 256;

    private static readonly object Gate = new();
    private static readonly List<ConsentGrant> Active = new();

    /// <summary>Records a grant (scoped, identity-bound, revocable). Returns the stored grant.
    /// grantedBy is REQUIRED (identity-bound: a household member never consumes another's
    /// consent); SessionScoped grants MUST be bound to a session id ("until the session
    /// ends" must be enforceable — revoke on session end).</summary>
    public static ConsentGrant Grant(string action, string target, ConsentScopeKind scope,
        string grantedBy, TimeSpan? duration = null, IReadOnlyList<string>? conditions = null,
        string? evidence = null, string? boundSessionId = null)
    {
        if (string.IsNullOrWhiteSpace(action)) throw new ArgumentException("Action is required.", nameof(action));
        if (string.IsNullOrWhiteSpace(grantedBy)) throw new ArgumentException("GrantedBy (principal) is required — consent is identity-bound.", nameof(grantedBy));
        if (scope == ConsentScopeKind.SessionScoped && string.IsNullOrWhiteSpace(boundSessionId))
            throw new ArgumentException("SessionScoped consent must be bound to a session id (revoke on session end).", nameof(boundSessionId));
        var grant = new ConsentGrant(action.Trim(), target?.Trim() ?? string.Empty, scope,
            DateTimeOffset.UtcNow, duration is { } d ? DateTimeOffset.UtcNow + d : null,
            conditions ?? [], evidence, grantedBy.Trim(), boundSessionId?.Trim());
        lock (Gate)
        {
            SweepExpiredLocked(DateTimeOffset.UtcNow);
            Active.Add(grant);
            while (Active.Count > MaxActiveGrants) Active.RemoveAt(0);
        }
        return grant;
    }

    /// <summary>
    /// Checks an active, unexpired, target-bound, IDENTITY-BOUND grant for the action.
    /// ADVISORY ONLY — this never authorizes: the AuthorizationPolicy result must still
    /// gate every execution (docs/realistic-autonomy.md §4; the LLM never authorizes).
    /// requestedBy is required: only grants made by the requesting principal match.
    /// </summary>
    public static ConsentGrant? FindActive(string action, string? target, string requestedBy, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(requestedBy)) throw new ArgumentException("RequestedBy (principal) is required.", nameof(requestedBy));
        var at = now ?? DateTimeOffset.UtcNow;
        lock (Gate)
        {
            SweepExpiredLocked(at);
            return Active.LastOrDefault(g =>
                string.Equals(g.Action, action, StringComparison.OrdinalIgnoreCase) &&
                g.Covers(target ?? string.Empty) &&
                string.Equals(g.GrantedBy, requestedBy, StringComparison.OrdinalIgnoreCase) &&
                (g.ExpiresAt is null || g.ExpiresAt > at));
        }
    }

    /// <summary>OneTime — consumed after use: removes the matching grant; true when consumed.</summary>
    public static bool Consume(string action, string? target, string requestedBy, DateTimeOffset? now = null)
    {
        var grant = FindActive(action, target, requestedBy, now);
        if (grant is null || grant.Scope != ConsentScopeKind.OneTime) return false;
        lock (Gate) { Active.Remove(grant); }
        return true;
    }

    /// <summary>Revokes every grant bound to one session — the session-end flow calls this.</summary>
    public static bool RevokeSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Session id is required.", nameof(sessionId));
        lock (Gate)
        {
            var removed = Active.RemoveAll(g => string.Equals(g.BoundSessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            return removed > 0;
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

    private static void SweepExpiredLocked(DateTimeOffset at)
    {
        Active.RemoveAll(g => g.ExpiresAt is { } exp && exp <= at);
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
