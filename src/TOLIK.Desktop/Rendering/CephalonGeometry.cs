namespace TOLIK.Desktop.Rendering;

/// <summary>
/// Framework-free point used by the parametric geometry (transferable to VR/3D later —
/// same math, different renderer).
/// </summary>
public readonly struct GeoPoint
{
    public readonly double X;
    public readonly double Y;

    public GeoPoint(double x, double y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>
/// Pure parametric geometry of the Cephalon: every shape the renderer draws is
/// produced here from (position, radius, angle, wobble) — no WPF types, no state.
/// This is the design source that ports to 3D/VR (docs/VR.md): layers/eye/shell
/// are drawn from a small set of parameters.
///
/// Functions fill caller-provided buffers so the render loop performs no
/// per-frame allocations.
/// </summary>
public static class CephalonGeometry
{
    public const double Tau = Math.PI * 2.0;
    public const int MaxBufferPoints = 128;
    public const int MaxHarmonics = 8;
    public const double MaxWobble = 0.2;

    /// <summary>Base body radius for a square canvas of side <paramref name="size"/>.</summary>
    public static double BaseRadius(double size) => size * 0.36;

    /// <summary>Wraps an angle to (-π, π].</summary>
    public static double WrapAngle(double angle) => Motion.CephalonParameters.Wrap(angle);

    /// <summary>Polar → cartesian (screen space, Y grows downward).</summary>
    public static GeoPoint Polar(double cx, double cy, double radius, double angle) =>
        new(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));

    /// <summary>
    /// Fills <paramref name="buffer"/> with points along a circular arc, radius
    /// modulated by a harmonic wobble (the "deformable shell").
    /// Wobble: r(θ) = radius * (1 + wobbleAmp * sin(harmonics·θ + wobblePhase)).
    /// Returns the number of points written (always in [2, buffer.Length]).
    /// </summary>
    public static int FillArcPoints(
        GeoPoint[] buffer,
        double cx,
        double cy,
        double radius,
        double startAngle,
        double sweep,
        int pointCount,
        double wobbleAmp,
        double wobblePhase,
        int harmonics)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length < 2)
        {
            return 0;
        }

        int n = Math.Clamp(pointCount, 2, buffer.Length);
        int h = Math.Clamp(harmonics, 1, MaxHarmonics);
        double wob = double.IsFinite(wobbleAmp) ? Math.Clamp(wobbleAmp, 0.0, MaxWobble) : 0.0;
        double r = double.IsFinite(radius) ? Math.Max(radius, 0.0) : 0.0;
        double a0 = double.IsFinite(startAngle) ? startAngle : 0.0;
        double sw = double.IsFinite(sweep) ? sweep : 0.0;
        double ph = double.IsFinite(wobblePhase) ? wobblePhase : 0.0;
        double ox = double.IsFinite(cx) ? cx : 0.0;
        double oy = double.IsFinite(cy) ? cy : 0.0;

        for (int i = 0; i < n; i++)
        {
            double t = (double)i / (n - 1);
            double a = a0 + sw * t;
            double ri = r * (1.0 + wob * Math.Sin((h * a) + ph));
            buffer[i] = Polar(ox, oy, ri, a);
        }
        return n;
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> with the vertices of a regular facet polygon
    /// (the angular iris). Returns the facet count written (clamped to [3, 24] and
    /// buffer capacity).
    /// </summary>
    public static int FillFacetPolygon(GeoPoint[] buffer, double cx, double cy, double radius, int facets, double rotation)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length < 3)
        {
            return 0;
        }

        int n = Math.Clamp(facets, 3, Math.Min(24, buffer.Length));
        double r = double.IsFinite(radius) ? Math.Max(radius, 0.0) : 0.0;
        double rot = double.IsFinite(rotation) ? rotation : 0.0;
        double ox = double.IsFinite(cx) ? cx : 0.0;
        double oy = double.IsFinite(cy) ? cy : 0.0;

        for (int i = 0; i < n; i++)
        {
            double a = rot + (Tau * i / n);
            buffer[i] = Polar(ox, oy, r, a);
        }
        return n;
    }

    /// <summary>Distance of a point from (cx, cy).</summary>
    public static double DistanceFromCenter(GeoPoint p, double cx, double cy)
    {
        double dx = p.X - cx;
        double dy = p.Y - cy;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
