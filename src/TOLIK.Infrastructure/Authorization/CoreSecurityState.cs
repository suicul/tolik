namespace TOLIK.Infrastructure.Authorization;

/// <summary>
/// Core security state machine (docs/SECURITY.md §27 Non-Destructive Lockdown;
/// ported from interface/policy.js coreState + interface/identity.js lockSecrets):
/// when a serious security event is detected the core enters LOCKED — sensitive
/// operations are denied (fail closed), authentication is refused, and the response
/// is DETECT → AUDIT → REVOKE → LOCK → NOTIFY, never destruction. Leaving LOCKED
/// requires an explicit owner action with an out-of-band unlock code; it is never
/// automatic and no role can unlock through the regular authorization path.
/// </summary>
public sealed class CoreSecurityState
{
    private readonly object _gate = new();

    public bool IsLocked { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset? LockedAt { get; private set; }

    /// <summary>Enters LOCKED (idempotent). Returns true when this call caused the transition.</summary>
    public bool EnterLocked(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Reason is required.", nameof(reason));
        lock (_gate)
        {
            if (IsLocked) return false;
            IsLocked = true;
            Reason = reason;
            LockedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    /// <summary>Explicit owner action leaves LOCKED (never automatic). Returns true when this call caused the transition.</summary>
    public bool TryUnlock(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Reason is required.", nameof(reason));
        lock (_gate)
        {
            if (!IsLocked) return false;
            IsLocked = false;
            Reason = null;
            LockedAt = null;
            return true;
        }
    }
}
