using TOLIK.Contracts;
using TOLIK.Desktop.Bridge;
using TOLIK.Desktop.Motion;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// The bridge is the ONLY place Core's contract meets the body's axes. Every mode
/// must reach its state; every numeric field must land on the axis it names,
/// clamped into the body's documented bounds; unknowns fail closed to calm Idle.
/// </summary>
public class ExpressionBridgeTests
{
    private static ExpressionFrame Neutral(ExpressionMode mode) => new(
        EyeScale: 1.0, EyeOpenness: 0.75, EyeRotation: 0, Asymmetry: 0,
        ShellExpansion: 0, ShellDeformation: 0, Rotation: 0,
        PulseAmplitude: 0.08, GlowIntensity: 0.25, ParticleDensity: 0.1,
        SpectrumIntensity: 0, MovementSpeed: 0.15, AttentionAngle: 0,
        AttentionIntensity: 0, TransitionEnergy: 0.3, Mode: mode);

    [Fact]
    public void All_seven_core_modes_reach_their_states_distinctly()
    {
        var expected = new Dictionary<ExpressionMode, CephalonState>
        {
            [ExpressionMode.Idle] = CephalonState.Idle,
            [ExpressionMode.Thinking] = CephalonState.Thinking,
            [ExpressionMode.Speaking] = CephalonState.Speaking,
            [ExpressionMode.Working] = CephalonState.Working,
            [ExpressionMode.Recovering] = CephalonState.Recovering,
            [ExpressionMode.Alert] = CephalonState.Alert,
            [ExpressionMode.Locked] = CephalonState.Locked,
        };

        var mapped = new HashSet<CephalonState>();
        foreach ((ExpressionMode mode, CephalonState want) in expected)
        {
            ExpressionDrive drive = ExpressionBridge.ToDrive(Neutral(mode));
            Assert.Equal(want, drive.State);
            Assert.Equal(want, ExpressionBridge.ToDrive(Neutral(mode)).State);
            mapped.Add(drive.State);
        }

        Assert.Equal(7, mapped.Count); // no two modes collapse into one state
    }

    [Fact]
    public void Neutral_wire_frame_maps_to_the_expected_drive()
    {
        ExpressionDrive drive = ExpressionBridge.ToDrive(Neutral(ExpressionMode.Idle));

        Assert.Equal(CephalonState.Idle, drive.State);
        Assert.Equal(0.75, drive.EyeOpen);               // eyeOpenness → eyelid
        Assert.Equal(1.0, drive.PupilScale);             // eyeScale → focus
        Assert.Equal(0.0, drive.ShellTilt);              // eyeRotation 0 → no tilt
        Assert.Equal(1.0, drive.ShellExpansionCenter);   // shellExpansion 0 → neutral breath
        Assert.Equal(0.0, drive.WobbleAmp);              // shellDeformation 0 → no extra wobble
        Assert.Equal(0.25, drive.GlowIntensity);         // glowIntensity → glow
        Assert.Equal(0.1, drive.ParticleEnergy);         // particleDensity → motes
        Assert.Equal(1.0, drive.DriftMultiplier);        // rotation 0 → baseline spin
        Assert.Equal(0.08, drive.PulseAmplitude);
        Assert.Equal(0.0, drive.Spectrum);                // honestly 0 until CE-3
        Assert.Equal(0.0, drive.PupilAnchorX);
        Assert.Equal(0.0, drive.PupilAnchorY);
        Assert.Equal(0.15, drive.MovementSpeed);
        Assert.Equal(0.3, drive.TransitionEnergy);
    }

    [Fact]
    public void Every_numeric_field_lands_on_its_real_axis()
    {
        var frame = new ExpressionFrame(
            EyeScale: 1.2, EyeOpenness: 0.4, EyeRotation: 0.15, Asymmetry: 0.9,
            ShellExpansion: 0.25, ShellDeformation: 0.5, Rotation: 0.2,
            PulseAmplitude: 0.6, GlowIntensity: 0.8, ParticleDensity: 0.7,
            SpectrumIntensity: 0.5, MovementSpeed: 0.6,
            AttentionAngle: Math.PI / 2.0, AttentionIntensity: 1.0,
            TransitionEnergy: 0.9, Mode: ExpressionMode.Working);

        ExpressionDrive drive = ExpressionBridge.ToDrive(frame);

        Assert.Equal(1.2, drive.PupilScale);
        Assert.Equal(0.4, drive.EyeOpen);
        // Core's full eyeRotation range maps onto the full tilt bound: 0.15 of 0.3.
        Assert.Equal(CephalonParameters.ShellTiltMax / 2.0, drive.ShellTilt, 9);
        // shellExpansion 0.25 → breathing center above neutral.
        Assert.Equal(1.0 + (0.25 * 0.16), drive.ShellExpansionCenter, 9);
        // shellDeformation 0.5 → half the wobble bound.
        Assert.Equal(CephalonParameters.WobbleMax * 0.5, drive.WobbleAmp, 9);
        Assert.Equal(0.8, drive.GlowIntensity);
        Assert.Equal(0.7, drive.ParticleEnergy);
        Assert.Equal(1.2, drive.DriftMultiplier); // 1.0 + rotation 0.2
        Assert.Equal(0.6, drive.PulseAmplitude);
        Assert.Equal(0.5, drive.Spectrum);
        // Attention: angle π/2 steers straight down-screen at full intensity.
        Assert.Equal(0.0, drive.PupilAnchorX, 9);
        Assert.Equal(1.0, drive.PupilAnchorY, 9);
        Assert.Equal(0.6, drive.MovementSpeed);
        Assert.Equal(0.9, drive.TransitionEnergy);
    }

    [Fact]
    public void Out_of_range_values_are_clamped_into_the_body_bounds()
    {
        var frame = new ExpressionFrame(
            EyeScale: 42.0, EyeOpenness: -3.0, EyeRotation: 0.3, Asymmetry: 2.0,
            ShellExpansion: 0.5, ShellDeformation: 1.0, Rotation: 0.5,
            PulseAmplitude: 9.0, GlowIntensity: 9.0, ParticleDensity: 9.0,
            SpectrumIntensity: 9.0, MovementSpeed: 9.0,
            AttentionAngle: -Math.PI, AttentionIntensity: 9.0,
            TransitionEnergy: 9.0, Mode: ExpressionMode.Alert);

        ExpressionDrive drive = ExpressionBridge.ToDrive(frame);

        Assert.Equal(CephalonParameters.PupilScaleMax, drive.PupilScale);
        Assert.Equal(0.0, drive.EyeOpen);
        Assert.Equal(CephalonParameters.ShellTiltMax, drive.ShellTilt);
        Assert.Equal(CephalonParameters.ShellExpansionMax, drive.ShellExpansionCenter);
        Assert.Equal(CephalonParameters.WobbleMax, drive.WobbleAmp);
        Assert.Equal(CephalonParameters.DriftMax, drive.DriftMultiplier);
        Assert.Equal(1.0, drive.GlowIntensity);
        Assert.Equal(1.0, drive.ParticleEnergy);
        Assert.Equal(1.0, drive.Spectrum);
        Assert.Equal(1.0, drive.MovementSpeed);
        Assert.Equal(1.0, drive.TransitionEnergy);
        // Attention anchors can never leave the pupil's travel range.
        Assert.InRange(drive.PupilAnchorX, -1.0, 1.0);
        Assert.InRange(drive.PupilAnchorY, -1.0, 1.0);
    }

    [Fact]
    public void Negative_drift_spin_reverses_within_bounds()
    {
        var frame = Neutral(ExpressionMode.Idle) with { Rotation = -0.4 };

        ExpressionDrive drive = ExpressionBridge.ToDrive(frame);

        Assert.Equal(0.6, drive.DriftMultiplier, 9);
    }

    [Fact]
    public void Zero_attention_intensity_anchors_the_gaze_at_center()
    {
        var frame = Neutral(ExpressionMode.Idle) with { AttentionAngle = 2.0, AttentionIntensity = 0.0 };

        ExpressionDrive drive = ExpressionBridge.ToDrive(frame);

        Assert.Equal(0.0, drive.PupilAnchorX, 12);
        Assert.Equal(0.0, drive.PupilAnchorY, 12);
    }

    [Fact]
    public void Null_frame_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ExpressionBridge.ToDrive(null!));
    }
}
