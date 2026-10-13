namespace TOLIK.Core;

/// <summary>
/// ProactivePolicy (docs/mind.md MI-7 — PLAN REFORM PRG-5a): КОГДА ума позволено
/// инициировать контакт — the deterministic budget/priority policy. The rules
/// (mind.md §7 + директива §23): (1) TOLIK NEVER interrupts the user — no proactive
/// message right after/right during a real interaction; (2) the daily proactive
/// budget is small and hard-capped (never spam); (3) the priorities: the system
/// signals (the failures, the stalls) MAY reach the user; the boredom/curiosity
/// driven messages are the LOWEST priority and capped hardest; (4) «я решил изменить
/// конфигурацию» NEVER goes out as a proactive message (the E2 gate already rejects
/// it — this policy is the mind-side second gate). The E2 NotificationGate is the
/// final gate; this policy is the MIND-SIDE decision — both must allow. PURE,
/// deterministic, unit-tested.
/// </summary>
public static class ProactivePolicy
{
    /// <summary>The quiet window: no proactive contact within N minutes after a real
    /// interaction (mind.md §7: TOLIK doesn't interrupt a conversation in progress).</summary>
    public const double QuietAfterInteractionMinutes = 5.0;

    /// <summary>The daily proactive budget (mind.md §7: малый и жёсткий — never spam).</summary>
    public const int DefaultDailyBudget = 6;

    public static int DailyBudget =>
        int.TryParse(Environment.GetEnvironmentVariable("TOLIK_MIND_PROACTIVE_DAILY"), out var v) && v >= 1 && v <= 50 ? v : DefaultDailyBudget;

    /// <summary>The proactive message kinds (the mind-side — the E2 gate re-validates).</summary>
    public enum ProactiveKind
    {
        /// <summary>A system signal worth surfacing (the task failed, the component is down) — the highest proactive priority.</summary>
        SystemSignal,
        /// <summary>A reminder due (the scheduled ones — the store-driven).</summary>
        Reminder,
        /// <summary>The boredom/curiosity-driven share (the lowest priority, capped hardest).</summary>
        CuriosityShare,
        /// <summary>Anything else the mind might want to say — DEFAULT DENY (§23: never spam).</summary>
        Other
    }

    /// <summary>The mind-side proactive decision. PURE — deterministic given
    /// (kind, state, budget state, now). The E2 gate is the FINAL gate; this policy
    /// is the MIND-SIDE second gate (both must allow — the defense in depth).</summary>
    public static ProactiveDecision Decide(
        ProactiveKind kind,
        MindSnapshot state,
        double minutesSinceInteraction,
        int proactivesToday,
        DateTimeOffset now,
        bool locked = false,
        int? dailyBudget = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var budget = dailyBudget ?? DailyBudget;

        // 1. The daily budget is HARD (mind.md §7: малый и жёсткий — never spam).
        if (proactivesToday >= budget)
            return new(false, "budget: the daily proactive budget is spent — silence until tomorrow.");

        // 2. TOLIK NEVER interrupts the user: no proactive contact within the quiet
        //    window after a real interaction (the user is present and in control).
        if (minutesSinceInteraction < QuietAfterInteractionMinutes)
            return new(false, "quiet: the user interacted recently — no proactive interruption.");

        // 3. The LOCKED/Emergency states silence the proactivity entirely (the real
        //    security/owner inputs — fed by the host, never read from the mind state).
        if (locked)
            return new(false, "locked: the Core is locked — nothing proactive leaves.");

        return kind switch
        {
            // 4. The system signals MAY reach the user (the honest failures/stalls).
            ProactiveKind.SystemSignal => new(true, "system signal: the honest failure/stall deserves surfacing."),
            // 5. The reminders are the scheduled store-driven ones.
            ProactiveKind.Reminder => new(true, "reminder: scheduled by the store."),
            // 6. The boredom/curiosity-driven shares: the LOWEST priority — allowed only
            //    when genuinely bored (the high boredom = the mind seeks activity) and
            //    the daily budget is not near-spent (the hard cap by 2/3 spent).
            ProactiveKind.CuriosityShare when state.Boredom >= 0.5 && proactivesToday * 3 < budget * 2
                => new(true, "curiosity share: genuinely bored, the budget has room."),
            // 7. Everything else: DEFAULT DENY (§23: never spam, never artificial chatter).
            _ => new(false, "not worth proactive contact — idle is a valid result (mind §8).")
        };
    }
}

/// <summary>The mind-side proactive decision (the E2 gate re-validates — both must allow).</summary>
public sealed record ProactiveDecision(bool Allowed, string Reason);
