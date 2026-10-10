namespace TOLIK.Desktop.Presence;

/// <summary>
/// Runtime presence mode of the Cephalon overlay.
/// Phase 1: switched manually via the tray menu. Automatic detection
/// (fullscreen/games) is Phase 4 — nothing here detects anything.
/// </summary>
public enum PresenceMode
{
    /// <summary>Default desktop presence. Full idle experience.</summary>
    Normal,

    /// <summary>Reduced motion and attention. Still visible.</summary>
    Quiet,

    /// <summary>Minimal motion, no particles. For focused work.</summary>
    Focus,

    /// <summary>Polite while the owner plays. Low FPS, no motion bursts, forced click-through.</summary>
    Gaming,

    /// <summary>Near-still while presenting/screen sharing. Lowest visible FPS, forced click-through.</summary>
    Presentation,

    /// <summary>Presence hidden: dormant, nearly frozen, captions off. Not an exit — a held breath.</summary>
    Hidden,

    /// <summary>Reserved alarm-adjacent mode: maximum responsiveness and interactivity.
    /// Phase 1 has no alarm source, so visuals stay in the calm blue family —
    /// red is never faked here (it arrives with real security events in Phase 2/3).</summary>
    Emergency,
}
