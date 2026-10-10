namespace TOLIK.Desktop.Motion;

/// <summary>
/// The single parametric description of the Cephalon body at one instant.
/// Everything the renderer draws is a function of these numbers — the same
/// parameter set can later drive a 3D/VR composition or a shader unchanged.
///
/// Every field has a documented range; <see cref="Clamp"/> enforces it after every
/// update and sanitizes NaN/Infinity (fail-closed to sane mid-range values).
/// </summary>
public sealed class CephalonParameters
{
    // Documented bounds (used by Clamp and by tests).
    public const double EyeOpenMin = 0.0, EyeOpenMax = 1.0;
    public const double PupilOffsetMin = -1.0, PupilOffsetMax = 1.0;
    public const double PupilScaleMin = 0.6, PupilScaleMax = 1.5;
    public const double GlowMin = 0.0, GlowMax = 1.0;
    public const double ParticleMin = 0.0, ParticleMax = 1.0;
    public const double ShellExpansionMin = 0.92, ShellExpansionMax = 1.08;
    public const double ShellTiltMin = -0.05, ShellTiltMax = 0.05;
    public const double WobbleMin = 0.0, WobbleMax = 0.06;
    public const double DriftMin = -1.5, DriftMax = 1.5;
    public const double SpectrumMin = 0.0, SpectrumMax = 1.0;

    /// <summary>Eyelid aperture (mechanical shutter, 0 = shut).</summary>
    public double EyeOpen = 1.0;

    /// <summary>Pupil gaze offset, fraction of max travel.</summary>
    public double PupilOffsetX;
    public double PupilOffsetY;

    /// <summary>Pupil dilation (focus).</summary>
    public double PupilScale = 1.0;

    /// <summary>Inner glow brightness.</summary>
    public double GlowIntensity = 0.45;

    /// <summary>Energy particle population/speed driver.</summary>
    public double ParticleEnergy = 0.5;

    /// <summary>Breathing scale of the shell (1 = neutral).</summary>
    public double ShellExpansion = 1.0;

    /// <summary>Silhouette tilt/compression (scales X up while Y compresses).</summary>
    public double ShellTilt;

    /// <summary>Deformable shell radial wobble amplitude.</summary>
    public double WobbleAmp;

    /// <summary>Layer rotation angles (radians, wrapped to (-π, π]).</summary>
    public double ShellRotation;
    public double Shell2Rotation;
    public double BracketRotation;
    public double IrisRotation;

    /// <summary>Wobble phase of the deformable shell.</summary>
    public double WobblePhase;

    /// <summary>Orientation drift multiplier applied to base rotation speeds.</summary>
    public double DriftMultiplier = 1.0;

    /// <summary>Voice/audio spectrum energy (0 = silent). CE-2 carries Core's composed
    /// value truthfully (honestly 0 until CE-3 wires the real TTS bands); the renderer
    /// adds EXACTLY zero effect for zero input.</summary>
    public double SpectrumIntensity;

    /// <summary>Enforces all documented bounds; NaN/Infinity fall back to sane defaults.</summary>
    public void Clamp()
    {
        EyeOpen = Clean(EyeOpen, 1.0, EyeOpenMin, EyeOpenMax);
        PupilOffsetX = Clean(PupilOffsetX, 0.0, PupilOffsetMin, PupilOffsetMax);
        PupilOffsetY = Clean(PupilOffsetY, 0.0, PupilOffsetMin, PupilOffsetMax);
        PupilScale = Clean(PupilScale, 1.0, PupilScaleMin, PupilScaleMax);
        GlowIntensity = Clean(GlowIntensity, 0.45, GlowMin, GlowMax);
        ParticleEnergy = Clean(ParticleEnergy, 0.5, ParticleMin, ParticleMax);
        ShellExpansion = Clean(ShellExpansion, 1.0, ShellExpansionMin, ShellExpansionMax);
        ShellTilt = Clean(ShellTilt, 0.0, ShellTiltMin, ShellTiltMax);
        WobbleAmp = Clean(WobbleAmp, 0.0, WobbleMin, WobbleMax);
        ShellRotation = Wrap(ShellRotation);
        Shell2Rotation = Wrap(Shell2Rotation);
        BracketRotation = Wrap(BracketRotation);
        IrisRotation = Wrap(IrisRotation);
        WobblePhase = Wrap(WobblePhase);
        DriftMultiplier = Clean(DriftMultiplier, 1.0, DriftMin, DriftMax);
        SpectrumIntensity = Clean(SpectrumIntensity, 0.0, SpectrumMin, SpectrumMax);
    }

    public void CopyTo(CephalonParameters other)
    {
        ArgumentNullException.ThrowIfNull(other);
        other.EyeOpen = EyeOpen;
        other.PupilOffsetX = PupilOffsetX;
        other.PupilOffsetY = PupilOffsetY;
        other.PupilScale = PupilScale;
        other.GlowIntensity = GlowIntensity;
        other.ParticleEnergy = ParticleEnergy;
        other.ShellExpansion = ShellExpansion;
        other.ShellTilt = ShellTilt;
        other.WobbleAmp = WobbleAmp;
        other.ShellRotation = ShellRotation;
        other.Shell2Rotation = Shell2Rotation;
        other.BracketRotation = BracketRotation;
        other.IrisRotation = IrisRotation;
        other.WobblePhase = WobblePhase;
        other.DriftMultiplier = DriftMultiplier;
        other.SpectrumIntensity = SpectrumIntensity;
    }

    private static double Clean(double value, double fallback, double min, double max) =>
        double.IsNaN(value) ? fallback : Math.Clamp(value, min, max);

    /// <summary>Wraps any finite angle to (-π, π]; non-finite → 0.</summary>
    public static double Wrap(double angle)
    {
        if (double.IsNaN(angle) || double.IsInfinity(angle))
        {
            return 0.0;
        }
        angle %= (Math.PI * 2.0);
        if (angle > Math.PI)
        {
            angle -= Math.PI * 2.0;
        }
        else if (angle <= -Math.PI)
        {
            angle += Math.PI * 2.0;
        }
        return angle;
    }
}
