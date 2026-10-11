using TOLIK.Desktop.Performance;
using TOLIK.Desktop.Rendering;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// Depth model of the volumetric Cephalon (owner tuning pass "add volume"):
/// plane ordering and bounds, gaze parallax, atmospheric falloff, and the
/// quality-tier shedding ladder — expensive depth effects shed first under
/// budget pressure, Minimal is the flat Phase 1 composition.
/// </summary>
public class CephalonDepthTests
{
    [Fact]
    public void Depth_planes_are_bounded_and_ordered_back_to_front()
    {
        double[] planes =
        {
            CephalonDepth.ZGlowCore,
            CephalonDepth.ZOuterRing,
            CephalonDepth.ZMiddleArcs,
            CephalonDepth.ZSpokes,
            CephalonDepth.ZBrackets,
            CephalonDepth.ZEye,
        };

        foreach (double z in planes)
        {
            Assert.InRange(z, CephalonDepth.ZMin, CephalonDepth.ZMax);
        }

        Assert.Equal(CephalonDepth.ZMin, CephalonDepth.ZGlowCore);
        Assert.Equal(CephalonDepth.ZMax, CephalonDepth.ZEye);
        for (int i = 1; i < planes.Length; i++)
        {
            Assert.True(planes[i - 1] < planes[i],
                $"plane {i - 1} ({planes[i - 1]}) must sit behind plane {i} ({planes[i]})");
        }
    }

    [Fact]
    public void Parallax_moves_near_planes_more_than_far_planes()
    {
        const double unit = 100.0;

        double far = Math.Abs(CephalonDepth.Parallax(CephalonDepth.ZOuterRing, 1.0, unit));
        double mid = Math.Abs(CephalonDepth.Parallax(CephalonDepth.ZMiddleArcs, 1.0, unit));
        double near = Math.Abs(CephalonDepth.Parallax(CephalonDepth.ZEye, 1.0, unit));

        Assert.True(far < mid);
        Assert.True(mid < near);
        Assert.Equal(0.0, CephalonDepth.Parallax(CephalonDepth.ZGlowCore, 1.0, unit), 9);
    }

    [Fact]
    public void Parallax_follows_the_gaze_sign_and_is_bounded()
    {
        const double unit = 90.0;

        double right = CephalonDepth.Parallax(CephalonDepth.ZEye, 1.0, unit);
        double left = CephalonDepth.Parallax(CephalonDepth.ZEye, -1.0, unit);

        Assert.Equal(-right, left, 9);                              // sign follows the gaze
        Assert.Equal(CephalonDepth.ParallaxStrength * unit, Math.Abs(right), 9); // |max| = strength × unit
        Assert.Equal(0.0, CephalonDepth.Parallax(CephalonDepth.ZEye, 0.0, unit), 9);

        // Oversized signals clamp instead of exploding.
        Assert.Equal(right, CephalonDepth.Parallax(CephalonDepth.ZEye, 25.0, unit), 9);
    }

    [Fact]
    public void Parallax_fails_closed_on_bad_input()
    {
        Assert.Equal(0.0, CephalonDepth.Parallax(double.NaN, 1.0, 100.0), 9);
        Assert.Equal(0.0, CephalonDepth.Parallax(CephalonDepth.ZEye, double.NaN, 100.0), 9);
        Assert.Equal(0.0, CephalonDepth.Parallax(CephalonDepth.ZEye, 1.0, double.NaN), 9);
        Assert.Equal(0.0, CephalonDepth.Parallax(double.NegativeInfinity, 1.0, 100.0), 9);
        Assert.Equal(0.0, CephalonDepth.Parallax(CephalonDepth.ZEye, 1.0, -1.0), 9); // negative unit fails closed

        // Out-of-range z clamps to the plane-space extremes, never explodes.
        Assert.Equal(
            CephalonDepth.Parallax(CephalonDepth.ZEye, 1.0, 100.0),
            CephalonDepth.Parallax(9.0, 1.0, 100.0), 9);
    }

    [Fact]
    public void Atmosphere_brightens_monotonically_from_far_to_near()
    {
        Assert.Equal(CephalonDepth.AtmosphereFloor, CephalonDepth.Atmosphere(0.0), 9);
        Assert.Equal(1.0, CephalonDepth.Atmosphere(1.0), 9);

        double prev = CephalonDepth.Atmosphere(0.0);
        for (int i = 1; i <= 10; i++)
        {
            double v = CephalonDepth.Atmosphere(i / 10.0);
            Assert.True(v >= prev, "atmosphere must never get dimmer as planes come nearer");
            Assert.InRange(v, CephalonDepth.AtmosphereFloor, 1.0);
            prev = v;
        }
    }

    [Fact]
    public void Atmosphere_fails_closed_on_bad_input()
    {
        Assert.Equal(CephalonDepth.AtmosphereFloor, CephalonDepth.Atmosphere(double.NaN), 9);
        Assert.Equal(CephalonDepth.AtmosphereFloor, CephalonDepth.Atmosphere(double.PositiveInfinity), 9);
        Assert.Equal(1.0, CephalonDepth.Atmosphere(4.2), 9);   // clamped high
        Assert.Equal(CephalonDepth.AtmosphereFloor, CephalonDepth.Atmosphere(-3.0), 9); // clamped low
    }

    [Fact]
    public void Shedding_keeps_the_full_depth_stack_at_High()
    {
        Assert.Equal(DepthShedding.All, CephalonDepth.Shed(RenderQuality.High));
    }

    [Fact]
    public void Shedding_Minimal_is_the_flat_Phase1_composition()
    {
        Assert.Equal(DepthShedding.None, CephalonDepth.Shed(RenderQuality.Minimal));
    }

    [Fact]
    public void Shedding_drops_atmosphere_first_then_occlusion_then_hot_core()
    {
        var medium = CephalonDepth.Shed(RenderQuality.Medium);
        Assert.False(medium.AtmosphericHalo); // the widest layer sheds first
        Assert.True(medium.HotCore);
        Assert.True(medium.OcclusionShadows);
        Assert.True(medium.DirectionalShading);

        var low = CephalonDepth.Shed(RenderQuality.Low);
        Assert.False(low.HotCore);
        Assert.False(low.OcclusionShadows);
        Assert.True(low.DirectionalShading); // the depth read survives longest
    }

    [Fact]
    public void Shedding_ladder_only_ever_sheds_effects()
    {
        var high = CephalonDepth.Shed(RenderQuality.High);
        var medium = CephalonDepth.Shed(RenderQuality.Medium);
        var low = CephalonDepth.Shed(RenderQuality.Low);
        var minimal = CephalonDepth.Shed(RenderQuality.Minimal);

        Assert.True(high.Contains(medium));
        Assert.True(medium.Contains(low));
        Assert.True(low.Contains(minimal));
    }

    [Fact]
    public void Shedding_fails_closed_for_undefined_tiers()
    {
        Assert.Equal(DepthShedding.None, CephalonDepth.Shed((RenderQuality)99));
        Assert.Equal(DepthShedding.None, CephalonDepth.Shed((RenderQuality)(-1)));
    }

    [Fact]
    public void DepthShedding_Contains_reports_subsets_correctly()
    {
        Assert.True(DepthShedding.All.Contains(DepthShedding.None));
        Assert.False(DepthShedding.None.Contains(DepthShedding.All));
        Assert.True(DepthShedding.All.Contains(DepthShedding.All));

        var a = new DepthShedding(AtmosphericHalo: true, HotCore: false, OcclusionShadows: false, DirectionalShading: true);
        var b = new DepthShedding(AtmosphericHalo: true, HotCore: false, OcclusionShadows: false, DirectionalShading: false);
        Assert.True(a.Contains(b));
        Assert.False(b.Contains(a));
    }
}
