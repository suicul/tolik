namespace TOLIK.Desktop.Motion;

/// <summary>
/// Color tone of the Cephalon. Blue is the primary family; red is strictly reserved
/// for real alarm/authorization states (Phase 2/3). Voice green is the reference
/// accent for active speech. Tone is mapped from state, never invented.
/// </summary>
public enum CephalonTone
{
    /// <summary>Calm blue — idle presence.</summary>
    Calm,

    /// <summary>Working blue — brighter, denser activity (thinking).</summary>
    Working,

    /// <summary>Voice green accent — speaking.</summary>
    Voice,

    /// <summary>Alarm red — denial / error. Only with a REAL authorization event.</summary>
    Error,

    /// <summary>Dull red — security lock. Fail-closed rendered visually.</summary>
    Locked,
}

/// <summary>
/// State → parameter trait mapping row. Immutable data: the renderer and animator
/// read these values; adding/adjusting a state means editing this table, not code
/// branches. This is the contract that keeps "no pre-baked mood animations" —
/// traits are targets the body eases toward, and procedural idle composes on top.
/// </summary>
/// <param name="EyeOpen">Eyelid aperture target (0 = shut, 1 = open).</param>
/// <param name="PupilScale">Pupil dilation target (1 = neutral).</param>
/// <param name="GlowIntensity">Inner glow base target (0..1).</param>
/// <param name="ParticleEnergy">Energy particle population/speed target (0..1).</param>
/// <param name="WobbleAmp">Base shell deformation amplitude (radial wobble, 0..0.06).</param>
/// <param name="MotionLevel">Scales idle stochastic amplitudes (0..~3).</param>
/// <param name="Tone">Color tone for this state.</param>
public sealed record StateTraits(
    double EyeOpen,
    double PupilScale,
    double GlowIntensity,
    double ParticleEnergy,
    double WobbleAmp,
    double MotionLevel,
    CephalonTone Tone);

/// <summary>
/// The state → traits mapping table. One row per state; no if/else chains in the
/// renderer. Unmapped states fail closed to Idle traits (calm, never a fake alarm).
/// </summary>
public static class StateTraitTable
{
    private static readonly Dictionary<CephalonState, StateTraits> Map = new()
    {
        //                    eye   pupil  glow  part  wobble motion tone
        [CephalonState.Idle]     = new(1.00, 1.00, 0.45, 0.50, 0.018, 1.0, CephalonTone.Calm),

        // Reserved rows — present so the machine is complete and extensible,
        // but Phase 1 runtime never activates them (no Core connection exists yet).
        [CephalonState.Thinking] = new(0.72, 0.85, 0.75, 0.90, 0.045, 2.2, CephalonTone.Working),
        [CephalonState.Speaking] = new(0.85, 1.10, 0.70, 0.75, 0.030, 1.8, CephalonTone.Voice),
        [CephalonState.Denied]   = new(0.50, 1.20, 0.85, 0.60, 0.050, 2.5, CephalonTone.Error),
        [CephalonState.Locked]   = new(0.18, 1.35, 0.35, 0.15, 0.010, 0.3, CephalonTone.Locked),
    };

    /// <summary>Number of mapped states (tests assert full coverage).</summary>
    public static int Count => Map.Count;

    public static bool TryGet(CephalonState state, out StateTraits traits) =>
        Map.TryGetValue(state, out traits!);

    /// <summary>Fail-closed lookup: unknown state → Idle traits (calm blue, never red).</summary>
    public static StateTraits GetSafe(CephalonState state) =>
        Map.TryGetValue(state, out var traits) ? traits : Map[CephalonState.Idle];
}
