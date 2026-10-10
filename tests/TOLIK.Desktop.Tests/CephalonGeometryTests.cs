using TOLIK.Desktop.Rendering;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// Pure parametric geometry: buffers are filled exactly, points stay finite,
/// wobble stays inside its amplitude, polygons sit on their radius.
/// These functions are the design source that later ports to VR/3D — they must
/// hold up under garbage input too.
/// </summary>
public class CephalonGeometryTests
{
    private const double Eps = 1e-9;

    [Fact]
    public void BaseRadius_scales_with_canvas_size()
    {
        Assert.Equal(144.0, CephalonGeometry.BaseRadius(400.0), 9);
        Assert.Equal(0.0, CephalonGeometry.BaseRadius(0.0), 9);
    }

    [Fact]
    public void Arc_points_are_finite_and_inside_the_wobble_bound()
    {
        var buffer = new GeoPoint[64];
        int n = CephalonGeometry.FillArcPoints(
            buffer, cx: 100, cy: 100, radius: 50,
            startAngle: 0, sweep: Math.PI / 3, pointCount: 32,
            wobbleAmp: 0.05, wobblePhase: 1.1, harmonics: 3);

        Assert.Equal(32, n);
        for (int i = 0; i < n; i++)
        {
            Assert.True(double.IsFinite(buffer[i].X));
            Assert.True(double.IsFinite(buffer[i].Y));
            double d = CephalonGeometry.DistanceFromCenter(buffer[i], 100, 100);
            Assert.InRange(d, 50.0 * 0.95 - Eps, 50.0 * 1.05 + Eps);
        }
    }

    [Fact]
    public void Arc_points_clamp_garbage_input()
    {
        var buffer = new GeoPoint[16];
        int n = CephalonGeometry.FillArcPoints(
            buffer, cx: double.NaN, cy: 0, radius: double.NaN,
            startAngle: double.NaN, sweep: double.PositiveInfinity,
            pointCount: -5, wobbleAmp: double.NaN, wobblePhase: double.NaN,
            harmonics: 999);

        Assert.InRange(n, 2, 16);
        for (int i = 0; i < n; i++)
        {
            Assert.True(double.IsFinite(buffer[i].X), $"point {i}.X not finite");
            Assert.True(double.IsFinite(buffer[i].Y), $"point {i}.Y not finite");
        }
    }

    [Fact]
    public void Facet_polygon_vertices_sit_on_the_radius()
    {
        var buffer = new GeoPoint[24];
        int n = CephalonGeometry.FillFacetPolygon(buffer, cx: 0, cy: 0, radius: 30, facets: 8, rotation: 0.4);

        Assert.Equal(8, n);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(30.0, CephalonGeometry.DistanceFromCenter(buffer[i], 0, 0), 9);
        }
    }

    [Fact]
    public void Facet_count_is_clamped_to_sane_bounds()
    {
        var buffer = new GeoPoint[4];
        Assert.Equal(3, CephalonGeometry.FillFacetPolygon(buffer, 0, 0, 10, facets: 1, rotation: 0));
        Assert.Equal(3, CephalonGeometry.FillFacetPolygon(buffer, 0, 0, 10, facets: 2, rotation: 0));
    }

    [Fact]
    public void Angles_wrap_into_the_pi_range()
    {
        Assert.Equal(10.5 - (4.0 * Math.PI), CephalonGeometry.WrapAngle(10.5), 9);
        Assert.InRange(CephalonGeometry.WrapAngle(-99.0), -Math.PI, Math.PI);
        Assert.Equal(0.0, CephalonGeometry.WrapAngle(double.NaN), 9);
    }
}
