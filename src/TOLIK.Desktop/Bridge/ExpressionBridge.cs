using TOLIK.Contracts;
using TOLIK.Desktop.Motion;

namespace TOLIK.Desktop.Bridge;

/// <summary>
/// Maps a parsed wire frame (<see cref="ExpressionFrame"/>) into ANIMATOR-SPACE
/// targets (<see cref="ExpressionDrive"/>) — the only place Core's expression
/// contract meets the body's axes. Pure, total and fail-closed: unknown modes
/// collapse to Idle, non-finite numbers to the neutral axis (defense in depth —
/// the wire parser already rejects both).
///
/// Mapping honesty (CE-2):
///   • Mode selects the state/traits/tone row — the renderer never guesses a mood;
///   • every numeric field lands on the axis it actually names: Core's eyeOpenness
///     opens the eyelid, glowIntensity lights the glow, shellDeformation deforms the
///     shell, rotation drifts the spin. Nothing is invented;
///   • Asymmetry is deliberately UNMAPPED — the Phase 1 body has no asymmetry
///     axis, and faking it into a wrong parameter would be a lie, not a feature;
///   • SpectrumIntensity travels to a REAL axis that adds exactly zero at zero —
///     CE-3 will feed it the true TTS bands.
/// </summary>
public static class ExpressionBridge
{
    /// <summary>Core mode → body state. Table, not branches; fail-closed to Idle.</summary>
    private static readonly Dictionary<ExpressionMode, CephalonState> ModeStates = new()
    {
        [ExpressionMode.Idle] = CephalonState.Idle,
        [ExpressionMode.Thinking] = CephalonState.Thinking,
        [ExpressionMode.Speaking] = CephalonState.Speaking,
        [ExpressionMode.Working] = CephalonState.Working,
        [ExpressionMode.Recovering] = CephalonState.Recovering,
        [ExpressionMode.Alert] = CephalonState.Alert,
        [ExpressionMode.Locked] = CephalonState.Locked,
    };

    /// <summary>Core's full eyeRotation range ([-0.3..0.3] rad) maps onto the full
    /// silhouette tilt bound ([-0.05..0.05]) — a visual shorthand, not a rotation.</summary>
    private const double TiltPerRotation = CephalonParameters.ShellTiltMax / 0.3;

    /// <summary>Core's shellExpansion ([-0.2..0.5], 0 = neutral) centers the breathing
    /// scale around 1.0; 0.16 keeps the top of Core's range inside the parameter bound.</summary>
    private const double ExpansionPerUnit = 0.16;

    public static ExpressionDrive ToDrive(ExpressionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        CephalonState state = ModeStates.TryGetValue(frame.Mode, out CephalonState mapped)
            ? mapped
            : CephalonState.Idle;

        // Attention is a gaze direction: the angle steers, the intensity walks the
        // eye out (screen convention: positive angle = clockwise from east).
        double intensity = Clamp01(frame.AttentionIntensity);
        double angle = Finite(frame.AttentionAngle, 0.0);
        double anchorX = Math.Clamp(intensity * Math.Cos(angle), -1.0, 1.0);
        double anchorY = Math.Clamp(intensity * Math.Sin(angle), -1.0, 1.0);

        return new ExpressionDrive(
            State: state,
            EyeOpen: Clamp01(frame.EyeOpenness),
            PupilScale: Math.Clamp(
                Finite(frame.EyeScale, 1.0),
                CephalonParameters.PupilScaleMin, CephalonParameters.PupilScaleMax),
            ShellTilt: Math.Clamp(
                Finite(frame.EyeRotation, 0.0) * TiltPerRotation,
                CephalonParameters.ShellTiltMin, CephalonParameters.ShellTiltMax),
            ShellExpansionCenter: Math.Clamp(
                1.0 + (Finite(frame.ShellExpansion, 0.0) * ExpansionPerUnit),
                CephalonParameters.ShellExpansionMin, CephalonParameters.ShellExpansionMax),
            WobbleAmp: Math.Clamp(
                Finite(frame.ShellDeformation, 0.0) * CephalonParameters.WobbleMax,
                CephalonParameters.WobbleMin, CephalonParameters.WobbleMax),
            GlowIntensity: Clamp01(frame.GlowIntensity),
            ParticleEnergy: Clamp01(frame.ParticleDensity),
            // Core's rotation is a drift request around the baseline spin: 0 → 1×
            // (the body keeps its Phase 1 rhythm), +0.5 rad/s → 1.5× (energized).
            DriftMultiplier: Math.Clamp(
                1.0 + Finite(frame.Rotation, 0.0),
                CephalonParameters.DriftMin, CephalonParameters.DriftMax),
            PulseAmplitude: Clamp01(frame.PulseAmplitude),
            Spectrum: Clamp01(frame.SpectrumIntensity),
            PupilAnchorX: anchorX,
            PupilAnchorY: anchorY,
            MovementSpeed: Clamp01(frame.MovementSpeed),
            TransitionEnergy: Clamp01(frame.TransitionEnergy));
    }

    private static double Clamp01(double value) =>
        Math.Clamp(Finite(value, 0.0), 0.0, 1.0);

    private static double Finite(double value, double fallback) =>
        double.IsFinite(value) ? value : fallback;
}
