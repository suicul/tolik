namespace TOLIK.Desktop.Render3D;

/// <summary>
/// Framework-free 3D vector — the design source for the future VR/Android
/// cephalon (docs/VR.md: one design source, same math, different renderer).
/// Pure math, no WPF types, no state; mirrors the CephalonGeometry.cs pattern.
/// Convention: right-handed, +Y up, +Z toward the viewer, body radius = 1.
/// </summary>
public readonly struct V3
{
    public readonly double X;
    public readonly double Y;
    public readonly double Z;

    public V3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static V3 Zero => default;

    public static V3 UnitX => new(1.0, 0.0, 0.0);

    public static V3 UnitY => new(0.0, 1.0, 0.0);

    public static V3 UnitZ => new(0.0, 0.0, 1.0);

    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static V3 operator -(V3 a) => new(-a.X, -a.Y, -a.Z);

    public static V3 operator *(V3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static V3 operator *(double s, V3 a) => a * s;

    public static double Dot(V3 a, V3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    /// <summary>Right-handed cross product a × b.</summary>
    public static V3 Cross(V3 a, V3 b) => new(
        (a.Y * b.Z) - (a.Z * b.Y),
        (a.Z * b.X) - (a.X * b.Z),
        (a.X * b.Y) - (a.Y * b.X));

    public double LengthSquared => Dot(this, this);

    public double Length => Math.Sqrt(LengthSquared);

    /// <summary>Unit vector; the zero vector (and non-finite input) fails closed to zero.</summary>
    public V3 Normalized()
    {
        double len = LengthSquared;
        if (!double.IsFinite(len) || len < 1e-24)
        {
            return Zero;
        }
        return this * (1.0 / Math.Sqrt(len));
    }

    public override string ToString() => FormattableString.Invariant($"({X:0.###}, {Y:0.###}, {Z:0.###})");
}
