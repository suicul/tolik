using TOLIK.Desktop.Motion;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// Parameter clamping is the last line of defense before rendering:
/// NaN/Infinity/extreme inputs must always come out finite and in range.
/// </summary>
public class ParameterClampTests
{
    [Fact]
    public void NaN_inputs_produce_finite_bounded_outputs()
    {
        var p = new CephalonParameters
        {
            EyeOpen = double.NaN,
            PupilOffsetX = double.NaN,
            PupilOffsetY = double.NaN,
            PupilScale = double.NaN,
            GlowIntensity = double.NaN,
            ParticleEnergy = double.NaN,
            ShellExpansion = double.NaN,
            ShellTilt = double.NaN,
            WobbleAmp = double.NaN,
            ShellRotation = double.NaN,
            Shell2Rotation = double.NaN,
            BracketRotation = double.NaN,
            IrisRotation = double.NaN,
            WobblePhase = double.NaN,
            DriftMultiplier = double.NaN,
        };
        p.Clamp();

        Assert.Equal(1.0, p.EyeOpen);
        Assert.Equal(0.0, p.PupilOffsetX);
        Assert.Equal(0.0, p.PupilOffsetY);
        Assert.Equal(1.0, p.PupilScale);
        Assert.Equal(0.45, p.GlowIntensity);
        Assert.Equal(0.5, p.ParticleEnergy);
        Assert.Equal(1.0, p.ShellExpansion);
        Assert.Equal(0.0, p.ShellTilt);
        Assert.Equal(0.0, p.WobbleAmp);
        Assert.Equal(0.0, p.ShellRotation);
        Assert.Equal(1.0, p.DriftMultiplier);
    }

    [Fact]
    public void Infinite_inputs_produce_finite_bounded_outputs()
    {
        var p = new CephalonParameters
        {
            EyeOpen = double.PositiveInfinity,
            PupilOffsetX = double.NegativeInfinity,
            PupilScale = double.PositiveInfinity,
            GlowIntensity = double.PositiveInfinity,
            ShellExpansion = double.NegativeInfinity,
            ShellTilt = double.PositiveInfinity,
            ShellRotation = double.PositiveInfinity,
            DriftMultiplier = double.NegativeInfinity,
        };
        p.Clamp();

        Assert.Equal(CephalonParameters.EyeOpenMax, p.EyeOpen);
        Assert.Equal(CephalonParameters.PupilOffsetMin, p.PupilOffsetX);
        Assert.Equal(CephalonParameters.PupilScaleMax, p.PupilScale);
        Assert.Equal(CephalonParameters.GlowMax, p.GlowIntensity);
        Assert.Equal(CephalonParameters.ShellExpansionMin, p.ShellExpansion);
        Assert.Equal(CephalonParameters.ShellTiltMax, p.ShellTilt);
        Assert.Equal(CephalonParameters.DriftMin, p.DriftMultiplier);
        Assert.InRange(p.ShellRotation, -Math.PI, Math.PI);
    }

    [Fact]
    public void Extreme_finite_values_are_clamped()
    {
        var p = new CephalonParameters
        {
            EyeOpen = 42.0,
            PupilOffsetX = -42.0,
            PupilScale = 100.0,
            GlowIntensity = -7.0,
            ShellExpansion = 9.9,
            ShellTilt = -9.9,
            WobbleAmp = 77.0,
        };
        p.Clamp();

        Assert.Equal(CephalonParameters.EyeOpenMax, p.EyeOpen);
        Assert.Equal(CephalonParameters.PupilOffsetMin, p.PupilOffsetX);
        Assert.Equal(CephalonParameters.PupilScaleMax, p.PupilScale);
        Assert.Equal(CephalonParameters.GlowMin, p.GlowIntensity);
        Assert.Equal(CephalonParameters.ShellExpansionMax, p.ShellExpansion);
        Assert.Equal(CephalonParameters.ShellTiltMin, p.ShellTilt);
        Assert.Equal(CephalonParameters.WobbleMax, p.WobbleAmp);
    }

    [Fact]
    public void Angles_wrap_into_the_pi_range()
    {
        var p = new CephalonParameters { ShellRotation = 10.5 };
        p.Clamp();

        Assert.InRange(p.ShellRotation, -Math.PI, Math.PI);
        Assert.Equal(10.5 - (4.0 * Math.PI), p.ShellRotation, 9);
    }

    [Fact]
    public void Clamp_is_idempotent()
    {
        var p = new CephalonParameters
        {
            EyeOpen = 3.7,
            PupilScale = 0.01,
            ShellRotation = 99.0,
            GlowIntensity = double.NaN,
        };
        p.Clamp();
        double[] first = Snapshot(p);
        p.Clamp();
        double[] second = Snapshot(p);

        Assert.Equal(first, second);
    }

    private static double[] Snapshot(CephalonParameters p) => new[]
    {
        p.EyeOpen, p.PupilOffsetX, p.PupilOffsetY, p.PupilScale,
        p.GlowIntensity, p.ParticleEnergy, p.ShellExpansion, p.ShellTilt,
        p.WobbleAmp, p.ShellRotation, p.Shell2Rotation, p.BracketRotation,
        p.IrisRotation, p.WobblePhase, p.DriftMultiplier,
    };
}
