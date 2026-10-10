namespace TOLIK.Desktop.Motion;

/// <summary>
/// A Core expression frame mapped into ANIMATOR-SPACE targets (see
/// TOLIK.Desktop.Bridge.ExpressionBridge). Events set TARGETS — the animator's own
/// easing turns the ~5 Hz event stream into continuous motion; nothing ever snaps.
///
/// Mapping honesty (CE-2):
///   • fields Core sends map only where they have a REAL axis in the body;
///   • SpectrumIntensity is honestly zero until CE-3 wires the TTS bands — the
///     drive carries it, the animator adds exactly zero effect for zero input;
///   • Asymmetry has no asymmetry axis in the current composition — deliberately
///     unmapped rather than faked into a wrong parameter.
/// </summary>
/// <param name="State">Cephalon state (tone/traits row) selected by Core's mode.</param>
/// <param name="EyeOpen">Eyelid target from Core eyeOpenness [0..1].</param>
/// <param name="PupilScale">Focus target from Core eyeScale [0.8..1.3].</param>
/// <param name="ShellTilt">Silhouette tilt from Core eyeRotation (visual shorthand).</param>
/// <param name="ShellExpansionCenter">Breathing center from Core shellExpansion.</param>
/// <param name="WobbleAmp">Shell deformation energy from Core shellDeformation [0..1],
/// mapped to the wobble bound and ADDED to the ambient wobble (Core's composer
/// delegates stochastic micro-movement to the renderer).</param>
/// <param name="GlowIntensity">Glow target from Core glowIntensity [0..1].</param>
/// <param name="ParticleEnergy">Particle target from Core particleDensity [0..1].</param>
/// <param name="DriftMultiplier">Layer-spin multiplier from Core rotation (1 = baseline).</param>
/// <param name="PulseAmplitude">Core pulse energy added to the procedural pulse.</param>
/// <param name="Spectrum">Core spectrum intensity (0 until CE-3 — rendered truthfully).</param>
/// <param name="PupilAnchorX">Gaze anchor from Core attentionAngle/Intensity (X).</param>
/// <param name="PupilAnchorY">Gaze anchor from Core attentionAngle/Intensity (Y).</param>
/// <param name="MovementSpeed">Core movement energy scaling idle stochasticity.</param>
/// <param name="TransitionEnergy">Scales the animator's easing rates (0.6..1.4).</param>
public sealed record ExpressionDrive(
    CephalonState State,
    double EyeOpen,
    double PupilScale,
    double ShellTilt,
    double ShellExpansionCenter,
    double WobbleAmp,
    double GlowIntensity,
    double ParticleEnergy,
    double DriftMultiplier,
    double PulseAmplitude,
    double Spectrum,
    double PupilAnchorX,
    double PupilAnchorY,
    double MovementSpeed,
    double TransitionEnergy);
