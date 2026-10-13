namespace TOLIK.Desktop.Presence;

/// <summary>
/// The desktop-awareness mode arbitration (CE-4 — PLAN REFORM PRG-2b, директива §12):
/// decides whether the presence mode should auto-switch from the observed desktop
/// state. PURE — deterministic, unit-tested, no side effects. The core rule: the
/// owner's MANUAL choice (the tray) is SACRED — the automation only ever touches a
/// mode it set itself (Auto). The auto rules: a fullscreen GAME → Gaming (quiet,
/// click-through, low FPS); any other fullscreen app → Presentation; no fullscreen →
/// back to Normal. Emergency/Hidden/Focus are never auto-entered and never
/// auto-left (they only respond to the owner).
/// </summary>
public static class ModeArbitration
{
    /// <summary>The auto-decision: the new mode, or null = keep the current one.
    /// Deterministic given (current, currentIsAuto, fullscreen, gameProcess).</summary>
    public static PresenceMode? Decide(PresenceMode current, bool currentIsAuto, bool fullscreen, bool gameProcess)
    {
        if (!currentIsAuto)
            return null; // the owner chose this mode — the automation never overrides

        // Emergency/Hidden/Focus are OWNER-ONLY modes: the automation never enters
        // them and never leaves them (they respond to the owner alone).
        if (current is not (PresenceMode.Normal or PresenceMode.Gaming or PresenceMode.Presentation))
            return null;

        if (fullscreen)
        {
            var target = gameProcess ? PresenceMode.Gaming : PresenceMode.Presentation;
            return current == target ? null : target;
        }

        // No fullscreen: the auto-set Gaming/Presentation ends; Normal stays.
        return current is PresenceMode.Gaming or PresenceMode.Presentation
            ? PresenceMode.Normal
            : null;
    }
}
