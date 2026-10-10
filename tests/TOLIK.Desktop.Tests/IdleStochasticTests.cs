using TOLIK.Desktop.Motion;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// Procedural idle: bounded stochasticity. Over long deterministic runs (seeded),
/// every motion must stay inside the configured amplitude/interval bounds, be
/// non-periodic, and freeze completely at zero motion scale.
/// </summary>
public class IdleStochasticTests
{
    private const double Dt = 1.0 / 60.0;
    private const int Steps = 30_000; // 500 simulated seconds
    private const double Eps = 1e-9;

    private static CephalonAnimator NewAnimator(ulong seed) =>
        new(IdleMotionConfig.Default, new XorShiftRandom(seed));

    [Fact]
    public void All_parameters_stay_finite_and_bounded_over_long_runs()
    {
        var animator = NewAnimator(0xA11CE);
        for (int step = 0; step < Steps; step++)
        {
            animator.Update(Dt);
            if (step % 100 == 0)
            {
                AssertBounds(animator.Parameters);
            }
        }
    }

    [Fact]
    public void Pupil_gaze_never_exceeds_the_saccade_bound()
    {
        var animator = NewAnimator(0xB0B);
        double maxSeen = 0.0;
        for (int step = 0; step < Steps; step++)
        {
            animator.Update(Dt);
            maxSeen = Math.Max(maxSeen, Math.Abs(animator.Parameters.PupilOffsetX));
            maxSeen = Math.Max(maxSeen, Math.Abs(animator.Parameters.PupilOffsetY));
        }

        Assert.True(maxSeen <= IdleMotionConfig.Default.SaccadeMaxOffset + 1e-6,
            $"max offset {maxSeen} exceeded bound");
    }

    [Fact]
    public void Blink_intervals_respect_configured_bounds()
    {
        var animator = NewAnimator(0xC1CADA);
        var cfg = IdleMotionConfig.Default;
        var blinkStarts = new List<double>();
        double previous = 1.0;

        for (int step = 0; step < Steps; step++)
        {
            animator.Update(Dt);
            double open = animator.Parameters.EyeOpen;
            if (previous >= 0.9 && open < 0.9)
            {
                blinkStarts.Add(animator.TimeSeconds);
            }
            previous = open;
        }

        // 500s at 3.5–9s intervals → expect dozens of blinks.
        Assert.True(blinkStarts.Count >= 20, $"too few blinks: {blinkStarts.Count}");

        for (int i = 1; i < blinkStarts.Count; i++)
        {
            double interval = blinkStarts[i] - blinkStarts[i - 1];
            Assert.InRange(interval, cfg.BlinkIntervalMin - 0.75, cfg.BlinkIntervalMax + 0.75);
        }
    }

    [Fact]
    public void Same_seed_produces_identical_trajectories()
    {
        var a = NewAnimator(0xD0D0);
        var b = NewAnimator(0xD0D0);
        var snapshots = new List<double[]>();

        for (int step = 0; step < 6_000; step++)
        {
            a.Update(Dt);
            b.Update(Dt);
            if (step % 500 == 0)
            {
                snapshots.Add(Snapshot(a.Parameters));
                Assert.Equal(snapshots[^1], Snapshot(b.Parameters));
            }
        }

        Assert.True(snapshots.Count >= 10);
    }

    [Fact]
    public void Motion_is_not_a_noticeable_fixed_loop()
    {
        var animator = NewAnimator(0xE11E);
        var rotationSamples = new HashSet<double>();
        var gazeSamples = new HashSet<double>();

        for (int step = 0; step < 36_000; step++) // 600s
        {
            animator.Update(Dt);
            if (step % 60 == 0) // once per simulated second
            {
                rotationSamples.Add(Math.Round(animator.Parameters.ShellRotation, 3));
                gazeSamples.Add(Math.Round(animator.Parameters.PupilOffsetX, 3));
            }
        }

        // Drifting orientation + scheduled micro-actions: sampled once per second
        // the trajectory must produce many distinct values, not a short loop.
        Assert.True(rotationSamples.Count > 300, $"rotation samples distinct: {rotationSamples.Count}");
        Assert.True(gazeSamples.Count > 40, $"gaze samples distinct: {gazeSamples.Count}");
    }

    [Fact]
    public void Zero_motion_scale_freezes_the_body()
    {
        var animator = NewAnimator(0xF00D);
        animator.MotionScale = 0.0;

        for (int step = 0; step < 2_000; step++) // ~33s: several scheduled actions
        {
            animator.Update(Dt);
        }

        CephalonParameters p = animator.Parameters;
        Assert.Equal(0.0, p.ShellRotation, 9);
        Assert.Equal(0.0, p.Shell2Rotation, 9);
        Assert.Equal(0.0, p.BracketRotation, 9);
        Assert.Equal(0.0, p.IrisRotation, 9);
        Assert.Equal(0.0, p.PupilOffsetX, 9);
        Assert.Equal(0.0, p.PupilOffsetY, 9);
        Assert.Equal(1.0, p.EyeOpen, 9);   // no blinks while dormant
        Assert.Equal(1.0, p.ShellExpansion, 9);
        Assert.Equal(0.0, p.ShellTilt, 9);
    }

    [Fact]
    public void State_changes_ease_smoothly_instead_of_snapping()
    {
        var animator = NewAnimator(0x51A7E);

        // Warm up in Idle.
        for (int step = 0; step < 600; step++)
        {
            animator.Update(Dt);
        }

        animator.SetState(CephalonState.Thinking);
        double previous = animator.Parameters.EyeOpen;
        double maxStepChange = 0.0;

        for (int step = 0; step < 30; step++) // half a second after the switch
        {
            animator.Update(Dt);
            maxStepChange = Math.Max(maxStepChange, Math.Abs(animator.Parameters.EyeOpen - previous));
            previous = animator.Parameters.EyeOpen;
        }

        // At 60Hz a smooth transition must move the aperture by tiny steps only.
        Assert.True(maxStepChange < 0.05, $"EyeOpen jumped by {maxStepChange}");
    }

    private static double[] Snapshot(CephalonParameters p) => new[]
    {
        p.EyeOpen, p.PupilOffsetX, p.PupilOffsetY, p.PupilScale,
        p.GlowIntensity, p.ParticleEnergy, p.ShellExpansion, p.ShellTilt,
        p.WobbleAmp, p.ShellRotation, p.Shell2Rotation, p.BracketRotation,
        p.IrisRotation, p.WobblePhase, p.DriftMultiplier,
    };

    private static void AssertBounds(CephalonParameters p)
    {
        Assert.True(double.IsFinite(p.EyeOpen) && p.EyeOpen is >= CephalonParameters.EyeOpenMin and <= CephalonParameters.EyeOpenMax);
        Assert.True(double.IsFinite(p.PupilOffsetX) && p.PupilOffsetX is >= CephalonParameters.PupilOffsetMin and <= CephalonParameters.PupilOffsetMax);
        Assert.True(double.IsFinite(p.PupilOffsetY) && p.PupilOffsetY is >= CephalonParameters.PupilOffsetMin and <= CephalonParameters.PupilOffsetMax);
        Assert.True(double.IsFinite(p.PupilScale) && p.PupilScale is >= CephalonParameters.PupilScaleMin and <= CephalonParameters.PupilScaleMax);
        Assert.True(double.IsFinite(p.GlowIntensity) && p.GlowIntensity is >= CephalonParameters.GlowMin and <= CephalonParameters.GlowMax);
        Assert.True(double.IsFinite(p.ParticleEnergy) && p.ParticleEnergy is >= CephalonParameters.ParticleMin and <= CephalonParameters.ParticleMax);
        Assert.True(double.IsFinite(p.ShellExpansion) && p.ShellExpansion is >= CephalonParameters.ShellExpansionMin and <= CephalonParameters.ShellExpansionMax);
        Assert.True(double.IsFinite(p.ShellTilt) && p.ShellTilt is >= CephalonParameters.ShellTiltMin and <= CephalonParameters.ShellTiltMax);
        Assert.True(double.IsFinite(p.WobbleAmp) && p.WobbleAmp is >= CephalonParameters.WobbleMin and <= CephalonParameters.WobbleMax);
        Assert.True(double.IsFinite(p.DriftMultiplier) && p.DriftMultiplier is >= CephalonParameters.DriftMin and <= CephalonParameters.DriftMax);
        Assert.InRange(p.ShellRotation, -Math.PI - Eps, Math.PI + Eps);
        Assert.InRange(p.Shell2Rotation, -Math.PI - Eps, Math.PI + Eps);
        Assert.InRange(p.BracketRotation, -Math.PI - Eps, Math.PI + Eps);
        Assert.InRange(p.IrisRotation, -Math.PI - Eps, Math.PI + Eps);
        Assert.InRange(p.WobblePhase, -Math.PI - Eps, Math.PI + Eps);
    }
}
