namespace TOLIK.Desktop.Motion;

/// <summary>
/// Bounded intervals/amplitudes of procedural idle motion. Every stochastic action
/// draws its interval and amplitude from these bounds — the body is alive but can
/// never become erratic or repetitive. Pure data, fully testable.
/// </summary>
/// <param name="SaccadeIntervalMin">Seconds between gaze shifts (min).</param>
/// <param name="SaccadeIntervalMax">Seconds between gaze shifts (max).</param>
/// <param name="SaccadeMaxOffset">Max gaze offset as a fraction of full pupil travel (0..1).</param>
/// <param name="SaccadeCenterChance">Chance a saccade returns gaze to center.</param>
/// <param name="BlinkIntervalMin">Seconds between shutter blinks (min).</param>
/// <param name="BlinkIntervalMax">Seconds between shutter blinks (max).</param>
/// <param name="BlinkDuration">One blink: close-and-open envelope length in seconds.</param>
/// <param name="BlinkCloseLevel">How far the shutter closes at blink peak (0..1 fraction of open).</param>
/// <param name="PulseIntervalMin">Seconds between energy pulses (min).</param>
/// <param name="PulseIntervalMax">Seconds between energy pulses (max).</param>
/// <param name="PulseMaxAmplitude">Max additive glow bump of a pulse.</param>
/// <param name="PulseReleaseRate">Exponential decay rate of the pulse envelope (1/s).</param>
/// <param name="DriftIntervalMin">Seconds between orientation drift retargets (min).</param>
/// <param name="DriftIntervalMax">Seconds between orientation drift retargets (max).</param>
/// <param name="DriftMaxVelocity">Max |drift multiplier target| applied to base rotation speeds.</param>
/// <param name="WobbleIntervalMin">Seconds between shell wobble retargets (min).</param>
/// <param name="WobbleIntervalMax">Seconds between shell wobble retargets (max).</param>
/// <param name="WobbleMaxAmplitude">Max radial wobble amplitude of the deformable shell.</param>
/// <param name="BreathingAmplitude">Shell breathing scale amplitude.</param>
/// <param name="BreathingPeriodSec">Breathing period in seconds.</param>
/// <param name="TiltAmplitude">Silhouette tilt amplitude.</param>
/// <param name="TiltPeriodSec">Tilt period in seconds.</param>
/// <param name="BaseOuterOmega">Outer ring base angular speed (rad/s; drift multiplies it).</param>
/// <param name="BaseMiddleOmega">Middle arc base angular speed (opposite direction).</param>
/// <param name="BaseBracketOmega">Inner bracket base angular speed.</param>
/// <param name="BaseIrisOmega">Iris facet base angular speed.</param>
public sealed record IdleMotionConfig(
    double SaccadeIntervalMin,
    double SaccadeIntervalMax,
    double SaccadeMaxOffset,
    double SaccadeCenterChance,
    double BlinkIntervalMin,
    double BlinkIntervalMax,
    double BlinkDuration,
    double BlinkCloseLevel,
    double PulseIntervalMin,
    double PulseIntervalMax,
    double PulseMaxAmplitude,
    double PulseReleaseRate,
    double DriftIntervalMin,
    double DriftIntervalMax,
    double DriftMaxVelocity,
    double WobbleIntervalMin,
    double WobbleIntervalMax,
    double WobbleMaxAmplitude,
    double BreathingAmplitude,
    double BreathingPeriodSec,
    double TiltAmplitude,
    double TiltPeriodSec,
    double BaseOuterOmega,
    double BaseMiddleOmega,
    double BaseBracketOmega,
    double BaseIrisOmega)
{
    /// <summary>Production tuning. All bounds chosen so motion reads as "alive but calm":
    /// micro-movements every few seconds, slow drifts, weak pulses, one soft blink
    /// every 3.5–9 s. Nothing here can loop noticeably — drift retargets and
    /// irrational-ish period ratios keep trajectories non-periodic.</summary>
    public static IdleMotionConfig Default { get; } = new(
        SaccadeIntervalMin: 2.0,
        SaccadeIntervalMax: 6.5,
        SaccadeMaxOffset: 0.55,
        SaccadeCenterChance: 0.35,
        BlinkIntervalMin: 3.5,
        BlinkIntervalMax: 9.0,
        BlinkDuration: 0.35,
        BlinkCloseLevel: 0.15,
        PulseIntervalMin: 4.5,
        PulseIntervalMax: 11.0,
        PulseMaxAmplitude: 0.25,
        PulseReleaseRate: 2.2,
        DriftIntervalMin: 3.0,
        DriftIntervalMax: 9.0,
        DriftMaxVelocity: 1.2,
        WobbleIntervalMin: 6.0,
        WobbleIntervalMax: 14.0,
        WobbleMaxAmplitude: 0.035,
        BreathingAmplitude: 0.018,
        BreathingPeriodSec: 6.8,
        TiltAmplitude: 0.02,
        TiltPeriodSec: 11.3,
        BaseOuterOmega: 0.055,
        BaseMiddleOmega: -0.085,
        BaseBracketOmega: 0.03,
        BaseIrisOmega: 0.012);
}
