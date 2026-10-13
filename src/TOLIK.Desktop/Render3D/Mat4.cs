namespace TOLIK.Desktop.Render3D;

/// <summary>
/// Framework-free 4×4 affine matrix (row-major, w = 1 point transforms — the
/// perspective divide lives explicitly in the projection stage, which keeps the
/// model transform exact and trivially testable). Pure math, no WPF, no state.
/// </summary>
public readonly struct Mat4
{
    public readonly double M00, M01, M02, M03;
    public readonly double M10, M11, M12, M13;
    public readonly double M20, M21, M22, M23;
    public readonly double M30, M31, M32, M33;

    private Mat4(
        double m00, double m01, double m02, double m03,
        double m10, double m11, double m12, double m13,
        double m20, double m21, double m22, double m23,
        double m30, double m31, double m32, double m33)
    {
        M00 = m00; M01 = m01; M02 = m02; M03 = m03;
        M10 = m10; M11 = m11; M12 = m12; M13 = m13;
        M20 = m20; M21 = m21; M22 = m22; M23 = m23;
        M30 = m30; M31 = m31; M32 = m32; M33 = m33;
    }

    public static Mat4 Identity => new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1);

    /// <summary>Standard matrix product. (a * b) applied to a point = a applied after b.</summary>
    public static Mat4 operator *(Mat4 a, Mat4 b) => new(
        (a.M00 * b.M00) + (a.M01 * b.M10) + (a.M02 * b.M20) + (a.M03 * b.M30),
        (a.M00 * b.M01) + (a.M01 * b.M11) + (a.M02 * b.M21) + (a.M03 * b.M31),
        (a.M00 * b.M02) + (a.M01 * b.M12) + (a.M02 * b.M22) + (a.M03 * b.M32),
        (a.M00 * b.M03) + (a.M01 * b.M13) + (a.M02 * b.M23) + (a.M03 * b.M33),
        (a.M10 * b.M00) + (a.M11 * b.M10) + (a.M12 * b.M20) + (a.M13 * b.M30),
        (a.M10 * b.M01) + (a.M11 * b.M11) + (a.M12 * b.M21) + (a.M13 * b.M31),
        (a.M10 * b.M02) + (a.M11 * b.M12) + (a.M12 * b.M22) + (a.M13 * b.M32),
        (a.M10 * b.M03) + (a.M11 * b.M13) + (a.M12 * b.M23) + (a.M13 * b.M33),
        (a.M20 * b.M00) + (a.M21 * b.M10) + (a.M22 * b.M20) + (a.M23 * b.M30),
        (a.M20 * b.M01) + (a.M21 * b.M11) + (a.M22 * b.M21) + (a.M23 * b.M31),
        (a.M20 * b.M02) + (a.M21 * b.M12) + (a.M22 * b.M22) + (a.M23 * b.M32),
        (a.M20 * b.M03) + (a.M21 * b.M13) + (a.M22 * b.M23) + (a.M23 * b.M33),
        (a.M30 * b.M00) + (a.M31 * b.M10) + (a.M32 * b.M20) + (a.M33 * b.M30),
        (a.M30 * b.M01) + (a.M31 * b.M11) + (a.M32 * b.M21) + (a.M33 * b.M31),
        (a.M30 * b.M02) + (a.M31 * b.M12) + (a.M32 * b.M22) + (a.M33 * b.M32),
        (a.M30 * b.M03) + (a.M31 * b.M13) + (a.M32 * b.M23) + (a.M33 * b.M33));

    /// <summary>Transforms a point (w = 1, affine row-vector-free convention).</summary>
    public static V3 TransformPoint(Mat4 m, V3 p) => new(
        (m.M00 * p.X) + (m.M01 * p.Y) + (m.M02 * p.Z) + m.M03,
        (m.M10 * p.X) + (m.M11 * p.Y) + (m.M12 * p.Z) + m.M13,
        (m.M20 * p.X) + (m.M21 * p.Y) + (m.M22 * p.Z) + m.M23);

    /// <summary>Right-handed rotation about +X (positive angle: +Z turns toward −Y).</summary>
    public static Mat4 RotateX(double radians)
    {
        if (!double.IsFinite(radians))
        {
            return Identity;
        }
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new Mat4(
            1, 0, 0, 0,
            0, c, -s, 0,
            0, s, c, 0,
            0, 0, 0, 1);
    }

    /// <summary>Right-handed rotation about +Y (positive angle: +Z turns toward +X).</summary>
    public static Mat4 RotateY(double radians)
    {
        if (!double.IsFinite(radians))
        {
            return Identity;
        }
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new Mat4(
             c, 0, s, 0,
             0, 1, 0, 0,
            -s, 0, c, 0,
             0, 0, 0, 1);
    }

    /// <summary>Right-handed rotation about +Z (positive angle: +X turns toward +Y).</summary>
    public static Mat4 RotateZ(double radians)
    {
        if (!double.IsFinite(radians))
        {
            return Identity;
        }
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new Mat4(
            c, -s, 0, 0,
            s, c, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1);
    }

    public static Mat4 Translate(double x, double y, double z) => new(
        1, 0, 0, x,
        0, 1, 0, y,
        0, 0, 1, z,
        0, 0, 0, 1);

    /// <summary>Non-uniform scale (x, y, z independent; non-finite fails closed to identity).</summary>
    public static Mat4 Scale(double sx, double sy, double sz)
    {
        if (!double.IsFinite(sx) || !double.IsFinite(sy) || !double.IsFinite(sz))
        {
            return Identity;
        }
        return new Mat4(
            sx, 0, 0, 0,
            0, sy, 0, 0,
            0, 0, sz, 0,
            0, 0, 0, 1);
    }
}
