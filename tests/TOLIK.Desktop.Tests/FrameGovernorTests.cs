using TOLIK.Desktop.Performance;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// FPS governor: interval gating, clamping, and budget-based quality adaptation
/// (degrade fast, recover slow, never below Minimal, never above High).
/// </summary>
public class FrameGovernorTests
{
    [Fact]
    public void First_frame_renders_then_gates_to_the_interval()
    {
        var governor = new FrameGovernor(30);

        Assert.True(governor.ShouldRender(0.0));
        Assert.False(governor.ShouldRender(0.02)); // 20ms < ~30.7ms interval
        Assert.True(governor.ShouldRender(0.034));
    }

    [Fact]
    public void TargetFps_is_clamped_to_sane_bounds()
    {
        Assert.Equal(FrameGovernor.MinFps, new FrameGovernor(0.1).TargetFps);
        Assert.Equal(FrameGovernor.MaxFps, new FrameGovernor(10_000).TargetFps);

        var governor = new FrameGovernor(30);
        governor.SetTargetFps(0.5);
        Assert.Equal(FrameGovernor.MinFps, governor.TargetFps);
        governor.SetTargetFps(500);
        Assert.Equal(FrameGovernor.MaxFps, governor.TargetFps);
    }

    [Fact]
    public void Gating_respects_a_new_target_fps()
    {
        var governor = new FrameGovernor(30);
        governor.SetTargetFps(10);

        Assert.True(governor.ShouldRender(0.0));
        Assert.False(governor.ShouldRender(0.05));
        Assert.True(governor.ShouldRender(0.1));
    }

    [Fact]
    public void Sustained_slow_frames_degrade_quality_to_Minimal()
    {
        var governor = new FrameGovernor(30); // budget ≈ 16.7ms
        double now = 0.0;

        for (int i = 0; i < 60; i++)
        {
            governor.OnFrameRendered(25.0, now); // way over budget
            now += 0.1;                          // step past the 1.5s change cooldown
        }

        Assert.Equal(RenderQuality.Minimal, governor.Quality);
    }

    [Fact]
    public void Fast_frames_recover_quality_to_High()
    {
        var governor = new FrameGovernor(30);
        double now = 0.0;

        // Degrade to Minimal first.
        for (int i = 0; i < 60; i++)
        {
            governor.OnFrameRendered(25.0, now);
            now += 0.1;
        }
        Assert.Equal(RenderQuality.Minimal, governor.Quality);

        // Feed comfortable frames long enough for full recovery.
        for (int i = 0; i < 150; i++)
        {
            governor.OnFrameRendered(4.0, now);
            now += 0.1;
        }

        Assert.Equal(RenderQuality.High, governor.Quality);
    }

    [Fact]
    public void Quality_never_leaves_the_defined_range()
    {
        var governor = new FrameGovernor(30);
        double now = 0.0;

        for (int i = 0; i < 200; i++)
        {
            double frameMs = i % 2 == 0 ? 40.0 : 2.0; // thrash the input
            governor.OnFrameRendered(frameMs, now);
            now += 0.1;
            Assert.True(Enum.IsDefined(governor.Quality));
        }
    }

    [Fact]
    public void Non_finite_frame_times_are_sanitized()
    {
        var governor = new FrameGovernor(30);
        governor.OnFrameRendered(double.NaN, 0.0);
        governor.OnFrameRendered(double.PositiveInfinity, 0.2);
        Assert.True(double.IsFinite(governor.EwmaFrameMs));
        Assert.Equal(0.0, governor.EwmaFrameMs, 9); // sanitized to 0
    }
}
