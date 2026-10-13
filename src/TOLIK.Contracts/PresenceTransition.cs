namespace TOLIK.Contracts;

/// <summary>
/// The formal presence-transition protocol (docs/identity-presence.md IP-5 — PLAN
/// REFORM PRG-8b): the handoff of TOLIK's primary presence from one device to
/// another. RULES: (1) the OLD device must explicitly confirm the handoff — never
/// a forced takeover; (2) the NEW device must confirm readiness BEFORE the transition
/// completes; (3) either side can CANCEL (the transition aborts); (4) the transition
/// is ATOMIC — the presence moves entirely or not at all; (5) the Core validates
/// both sides (the authorization pipeline). Bounded (the transition expires after
/// 60 s — no stale handoffs). Thread-safe.
/// </summary>
public enum PresenceTransitionState
{
    Initiated,
    OldDeviceConfirmed,
    NewDeviceReady,
    Completed,
    Cancelled,
    Expired
}

public sealed record PresenceTransitionStateData(
    string TransitionId,
    string FromDeviceId,
    string ToDeviceId,
    PresenceTransitionState State,
    DateTimeOffset InitiatedAt,
    DateTimeOffset ExpiresAt)
{
    public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;
}

/// <summary>
/// The presence-transition engine: the formal handoff protocol. Deterministic;
/// thread-safe; the Core validates both sides. The transition is Initiated by the
/// Core (the owner or the policy), then: the OLD device confirms → the NEW device
/// confirms readiness → the transition COMPLETES. Either side can cancel. The
/// transition expires after 60 s (no stale handoffs).
/// </summary>
public sealed class PresenceTransitionEngine
{
    public const int ExpirySeconds = 60;

    private readonly object _gate = new();
    private readonly Dictionary<string, PresenceTransitionStateData> _transitions = new(StringComparer.Ordinal);

    /// <summary>Initiates a transition. Returns the transition data or null if
    /// another transition is already in progress (one at a time).</summary>
    public PresenceTransitionStateData? Initiate(string fromDeviceId, string toDeviceId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(fromDeviceId) || string.IsNullOrWhiteSpace(toDeviceId))
            throw new ArgumentException("Both device IDs are required.");
        if (fromDeviceId == toDeviceId)
            throw new ArgumentException("The source and target devices must differ.");
        lock (_gate)
        {
            ExpireStaleLocked(now);
            if (_transitions.Any(t => t.Value.State is not (PresenceTransitionState.Completed or PresenceTransitionState.Cancelled or PresenceTransitionState.Expired)))
                return null; // another transition in progress — one at a time
            var transition = new PresenceTransitionStateData(
                Guid.NewGuid().ToString("N"), fromDeviceId, toDeviceId,
                PresenceTransitionState.Initiated, now, now.AddSeconds(ExpirySeconds));
            _transitions[transition.TransitionId] = transition;
            return transition;
        }
    }

    /// <summary>The OLD device confirms the handoff (the release of the presence).</summary>
    public PresenceTransitionStateData? OldDeviceConfirm(string transitionId, string deviceId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_transitions.TryGetValue(transitionId, out var transition)) return null;
            if (transition.FromDeviceId != deviceId || transition.State != PresenceTransitionState.Initiated) return null;
            var updated = transition with { State = PresenceTransitionState.OldDeviceConfirmed };
            _transitions[transitionId] = updated;
            return updated;
        }
    }

    /// <summary>The NEW device confirms readiness (the acquisition of the presence).</summary>
    public PresenceTransitionStateData? NewDeviceReady(string transitionId, string deviceId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_transitions.TryGetValue(transitionId, out var transition)) return null;
            if (transition.ToDeviceId != deviceId || transition.State != PresenceTransitionState.OldDeviceConfirmed) return null;
            var updated = transition with { State = PresenceTransitionState.Completed };
            _transitions[transitionId] = updated;
            return updated;
        }
    }

    /// <summary>Cancels the transition (either side can cancel — the transition aborts).</summary>
    public bool Cancel(string transitionId)
    {
        lock (_gate)
        {
            if (!_transitions.TryGetValue(transitionId, out var transition)) return false;
            if (transition.State is PresenceTransitionState.Completed or PresenceTransitionState.Expired) return false;
            _transitions[transitionId] = transition with { State = PresenceTransitionState.Cancelled };
            return true;
        }
    }

    public PresenceTransitionStateData? Find(string transitionId)
    {
        lock (_gate)
        {
            if (!_transitions.TryGetValue(transitionId, out var transition)) return null;
            ExpireStaleLocked(DateTimeOffset.UtcNow);
            return _transitions.TryGetValue(transitionId, out var updated) ? updated : null;
        }
    }

    private void ExpireStaleLocked(DateTimeOffset now)
    {
        foreach (var (id, transition) in _transitions)
        {
            if (transition.State is PresenceTransitionState.Initiated or PresenceTransitionState.OldDeviceConfirmed or PresenceTransitionState.NewDeviceReady)
            {
                if (transition.IsExpired)
                    _transitions[id] = transition with { State = PresenceTransitionState.Expired };
            }
        }
    }
}
