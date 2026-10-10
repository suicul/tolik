namespace TOLIK.Contracts;

/// <summary>System alarm level — the PRIORITY source. Core sets it; the Expression
/// Composer's emotional model NEVER determines the real severity (директива §10).</summary>
public enum SystemSignalLevel
{
    None = 0,
    Warning = 1,
    Error = 2,
    Critical = 3
}

/// <summary>Dominant visual mode hint — the renderer's color vocabulary maps this to
/// its iris/glow palette; the SHAPE parameters carry the blend (color is never the
/// only emotion carrier — директива §2). Not an animation clip selector.</summary>
public enum ExpressionMode
{
    Idle,
    Thinking,
    Speaking,
    Working,
    Recovering,
    Alert,
    Locked
}

/// <summary>
/// Typed Expression State (директива §4, CE-2): a snapshot of what TOLIK's body should
/// express — built ONLY from real sources (mind state, voice telemetry, system health,
/// task lifecycle, presence), never from the model's opinion. Values are normalized
/// [0..1] (bools where meaningful); the state is updated by a separate runtime
/// component (the State Watcher) — the Renderer never invents emotional state.
/// Emotional parameters: only what the architecture needs — amusement/curiosity/
/// frustration/confidence/uncertainty map from Mind (MI-0..2) and events; the full
/// human emotion set exists in the future mood layer, not here.
/// </summary>
public sealed record ExpressionState(
    // Cognitive (engineering parameters of behavior, not claims of consciousness).
    double Attention,
    double Thinking,
    double Concentration,
    // Emotional (the needed subset; Mind MI-0..2 maps in).
    double Amusement,
    double Curiosity,
    double Frustration,
    double Confidence,
    double Uncertainty,
    // Social.
    double Engagement,
    // Voice (the real TTS stream — директива §9: not an approximation fired at speech start).
    bool IsSpeaking,
    double Amplitude,
    double LowBand,
    double MidBand,
    double HighBand,
    // System (the alarm priority source — Core sets it).
    SystemSignalLevel SignalLevel,
    double Processing,
    double TaskProgress,
    double Recovery,
    bool Locked)
{
    /// <summary>The honest default: quiet idle, no signals, no fake alarm.</summary>
    public static ExpressionState Idle { get; } = new(
        Attention: 0, Thinking: 0, Concentration: 0,
        Amusement: 0, Curiosity: 0, Frustration: 0, Confidence: 0, Uncertainty: 0,
        Engagement: 0,
        IsSpeaking: false, Amplitude: 0, LowBand: 0, MidBand: 0, HighBand: 0,
        SignalLevel: SystemSignalLevel.None,
        Processing: 0, TaskProgress: 0, Recovery: 0, Locked: false);

    /// <summary>Normalization rule: every double clamps to [0..1]; non-finite values
    /// clamp to 0 (a corrupted input never renders as a spike — fail closed to neutral).</summary>
    public ExpressionState Normalized() => new(
        Clamp01(Attention), Clamp01(Thinking), Clamp01(Concentration),
        Clamp01(Amusement), Clamp01(Curiosity), Clamp01(Frustration),
        Clamp01(Confidence), Clamp01(Uncertainty),
        Clamp01(Engagement),
        IsSpeaking, Clamp01(Amplitude), Clamp01(LowBand), Clamp01(MidBand), Clamp01(HighBand),
        SignalLevel,
        Clamp01(Processing), Clamp01(TaskProgress), Clamp01(Recovery),
        Locked);

    private static double Clamp01(double v) => double.IsFinite(v) ? Math.Clamp(v, 0.0, 1.0) : 0.0;
}

/// <summary>
/// The renderer's input (директива §5, CE-2): the composed visual-behavior parameters.
/// Shape carries the expression; Mode is only a color-vocabulary hint. Every parameter
/// has an explicit bounded range; the composer clamps — no NaN/Infinity ever reaches
/// the renderer.
/// </summary>
public sealed record ExpressionOutput(
    double EyeScale,           // [0.8..1.3]
    double EyeOpenness,        // [0..1]
    double EyeRotation,        // [-0.3..0.3] rad
    double Asymmetry,          // [0..1]
    double ShellExpansion,     // [-0.2..0.5]
    double ShellDeformation,   // [0..1]
    double Rotation,           // [-0.5..0.5] rad/s drift
    double PulseAmplitude,     // [0..1]
    double GlowIntensity,      // [0..1]
    double ParticleDensity,    // [0..1]
    double SpectrumIntensity,  // [0..1]
    double MovementSpeed,      // [0..1]
    double AttentionAngle,     // [-pi..pi] rad
    double AttentionIntensity, // [0..1]
    double TransitionEnergy,   // [0..1]
    ExpressionMode Mode = ExpressionMode.Idle)
{
    public static ExpressionOutput Neutral { get; } = new(
        EyeScale: 1.0, EyeOpenness: 0.75, EyeRotation: 0,
        Asymmetry: 0, ShellExpansion: 0, ShellDeformation: 0,
        Rotation: 0, PulseAmplitude: 0.08, GlowIntensity: 0.25,
        ParticleDensity: 0.1, SpectrumIntensity: 0, MovementSpeed: 0.15,
        AttentionAngle: 0, AttentionIntensity: 0, TransitionEnergy: 0.3);

    /// <summary>Parameter bounds — the composer clamps to these; the renderer trusts them.</summary>
    public ExpressionOutput Clamped() => new(
        Math.Clamp(EyeScale, 0.8, 1.3),
        Math.Clamp(EyeOpenness, 0.0, 1.0),
        Math.Clamp(EyeRotation, -0.3, 0.3),
        Math.Clamp(Asymmetry, 0.0, 1.0),
        Math.Clamp(ShellExpansion, -0.2, 0.5),
        Math.Clamp(ShellDeformation, 0.0, 1.0),
        Math.Clamp(Rotation, -0.5, 0.5),
        Math.Clamp(PulseAmplitude, 0.0, 1.0),
        Math.Clamp(GlowIntensity, 0.0, 1.0),
        Math.Clamp(ParticleDensity, 0.0, 1.0),
        Math.Clamp(SpectrumIntensity, 0.0, 1.0),
        Math.Clamp(MovementSpeed, 0.0, 1.0),
        Math.Clamp(AttentionAngle, -Math.PI, Math.PI),
        Math.Clamp(AttentionIntensity, 0.0, 1.0),
        Math.Clamp(TransitionEnergy, 0.0, 1.0),
        Mode);

    /// <summary>Fail-closed (директива §18): non-finite parameters collapse to neutral —
    /// a corrupted computation never renders as a spike.</summary>
    public bool IsFinite =>
        double.IsFinite(EyeScale) && double.IsFinite(EyeOpenness) && double.IsFinite(EyeRotation) &&
        double.IsFinite(Asymmetry) && double.IsFinite(ShellExpansion) && double.IsFinite(ShellDeformation) &&
        double.IsFinite(Rotation) && double.IsFinite(PulseAmplitude) && double.IsFinite(GlowIntensity) &&
        double.IsFinite(ParticleDensity) && double.IsFinite(SpectrumIntensity) && double.IsFinite(MovementSpeed) &&
        double.IsFinite(AttentionAngle) && double.IsFinite(AttentionIntensity) && double.IsFinite(TransitionEnergy);
}
