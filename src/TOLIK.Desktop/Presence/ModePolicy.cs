namespace TOLIK.Desktop.Presence;

/// <summary>
/// Per-mode policy: how present TOLIK is allowed to be.
/// Pure data — no detection, no side effects, trivially testable.
/// </summary>
/// <param name="TargetFps">Render FPS ceiling the governor enforces for this mode.</param>
/// <param name="IdleIntensity">Scales idle motion amplitudes (saccades, pulses, drift). 0 = frozen.</param>
/// <param name="Interactivity">Reserved policy knob for Phase 2 wiring (how much the overlay invites interaction).</param>
/// <param name="AllowParticles">Whether energy particles are drawn at all.</param>
/// <param name="AllowMotion">Whether the body may move at all (Hidden = false → dormant pose).</param>
/// <param name="ShowCaption">Whether the small "TOLIK · mode" caption is drawn.</param>
/// <param name="AllowDebugOverlay">Whether the diagnostics overlay may be shown in this mode.</param>
/// <param name="ForcePassive">True → overlay is click-through regardless of the user's interactive preference.</param>
public sealed record ModePolicy(
    double TargetFps,
    double IdleIntensity,
    double Interactivity,
    bool AllowParticles,
    bool AllowMotion,
    bool ShowCaption,
    bool AllowDebugOverlay,
    bool ForcePassive);
