using TOLIK.Desktop.Performance;

namespace TOLIK.Desktop.Render3D;

/// <summary>Identifies one procedural body part of the Cephalon mesh.</summary>
public enum PartKind
{
    /// <summary>Far hull band — 6 arc segments, slow spin, wobbles.</summary>
    RingOuter,

    /// <summary>Mid chassis arcs — 3 arc segments, counter-spin, wobbles, spectrum layer.</summary>
    RingMiddle,

    /// <summary>The outer anchor: 3 wide arcs of the OUTERMOST ring. Replaces the
    /// retired corner brackets (owner verdict: "ограничивающие рамки, их прям
    /// видно") with a natural body part — a tilted, slowly rotating cage arc.</summary>
    AnchorArcs,

    /// <summary>The eye globe — a real lat-long sphere.</summary>
    EyeSphere,

    /// <summary>The facet iris — the octagonal cut-gem cap on the sphere front.</summary>
    Iris,

    /// <summary>The pupil — a dark cap offset by the gaze.</summary>
    Pupil,

    /// <summary>Hemispherical shutter lids (top half).</summary>
    LidTop,

    /// <summary>Hemispherical shutter lids (bottom half).</summary>
    LidBottom,
}

/// <summary>
/// Shading material of a part. The pipeline computes a scalar shade; the WPF
/// drawer maps (tone, material, shade bucket) to a cached frozen brush. The
/// palette itself is unchanged (CephalonPalette) — blue family, red only for
/// Error/Locked tones.
/// </summary>
public enum MaterialKind
{
    ShellOuter = 0,
    ShellMiddle = 1,
    ShellAnchor = 2,
    Sclera = 3,
    Iris = 4,
    Pupil = 5,
    Lid = 6,

    Count = 7,
}

/// <summary>
/// All numbers that define the 3D Cephalon — the single design source for the
/// future VR/Android body (docs/VR.md). Body space: radius 1 = the body radius,
/// +Y up, +Z toward the viewer; camera at (0, 0, CameraDist) looking down −Z.
/// Framework-free, pure constants.
/// </summary>
public static class Cephalon3D
{
    // ---- camera ----

    /// <summary>Camera distance from the body origin, in body-radius units.</summary>
    public const double CameraDist = 3.9;

    /// <summary>Perspective focal length as a fraction of the canvas side.</summary>
    public const double FocalPerSize = 1.25;

    /// <summary>Structural default body tilt about X: the body is seen slightly
    /// from above (the top tips away) — volume is the default, not an effect.</summary>
    public const double BodyTiltX = -0.16;

    /// <summary>Structural default body tilt about Y — a slight turn toward the key light.</summary>
    public const double BodyTiltY = 0.08;

    /// <summary>Max gaze-following body turn about Y, radians (structural head-look).</summary>
    public const double GazeBodyYaw = 0.05;

    /// <summary>Max gaze-following body pitch about X, radians.</summary>
    public const double GazeBodyPitch = 0.035;

    /// <summary>Max iris/pupil cap rotation arc on the sphere per unit gaze, radians.</summary>
    public const double GazeCapArc = 0.34;

    /// <summary>Key light: direction from a surface point TOWARD the light
    /// (upper-left front — the depth pass light, now the real light).</summary>
    public static readonly V3 KeyLightDir = new V3(-0.45, 0.55, 0.62).Normalized();

    /// <summary>World-z mapped to CephalonDepth.Atmosphere(0) (farthest).</summary>
    public const double ZRangeMin = -1.0;

    /// <summary>World-z mapped to CephalonDepth.Atmosphere(1) (nearest).</summary>
    public const double ZRangeMax = 1.0;

    // ---- part placement (z-depths in body-radius units, ±0.3..1.0 of the radius) ----

    public const double ZOuter = -0.62;
    public const double ZMiddle = -0.34;
    public const double ZAnchor = 0.18;
    public const double ZEye = 0.44;

    public const double EyeRadius = 0.52;
    public const double LidRadiusFactor = 1.035;

    // ---- ring band shapes ----

    public const double OuterRadius = 1.06, OuterWidth = 0.16, OuterCamber = 0.10;
    public const double OuterTiltX = 0.244;   // ≈ +14°
    public const double OuterTiltY = 0.0;

    public const double MidRadius = 0.88, MidWidth = 0.20, MidCamber = 0.08;
    public const double MidTiltX = -0.209;   // ≈ −12°
    public const double MidTiltY = 0.157;    // ≈ +9°

    public const double AnchorRadius = 1.18, AnchorWidth = 0.10, AnchorCamber = 0.14;
    public const double AnchorTiltX = 0.419; // ≈ +24°
    public const double AnchorTiltY = 0.0;

    // ---- eye details ----

    public const double IrisRadiusFrac = 0.66;  // of the eye radius (unchanged identity)
    public const double IrisInnerFrac = 0.36;
    public const double IrisLift = 0.05;        // the gem sits proud of the globe
    public const double IrisApexLift = 0.07;
    public const int IrisFacets = 8;            // the octagonal signature
    public const double PupilRadiusFrac = 0.30; // of the eye radius (unchanged identity)
    public const double PupilApexLift = 0.03;

    /// <summary>Lid travel: parked edge angle (fully open) and closed edge angle
    /// (fully shut), degrees from the eye pole.</summary>
    public const double LidParkedDeg = 6.0;
    public const double LidClosedDeg = 94.0;

    // ---- motes ----

    public const double MoteRadiusMin = 0.84;
    public const double MoteRadiusMax = 1.16;
    public const double MoteSizePerFocal = 0.010;
    public const int MoteSeedCount = 28;

    // ---- flat shading / emissive grade ----

    /// <summary>Shade quantization buckets per material (frozen brushes are
    /// cached per bucket — the frozen-brush reuse pattern survives).</summary>
    public const int ShadeBuckets = 32;

    /// <summary>Per-material emissive shading definition. The shade floor IS the
    /// emissive blue — the Cephalon glows; the lit face approaches the hot blue.</summary>
    public readonly record struct MaterialShade(
        double FloorBase,
        double GlowBoost,
        double SpectrumBoost,
        double SpectrumShimmerRate,
        bool UseAtmosphere,
        double Alpha);

    private static readonly MaterialShade[] Materials =
    {
        //                 floor  +glow  +spec  shimmer  atmos  alpha
        new( /* ShellOuter */ 0.30, 0.22, 0.00, 0.0, true,  0.92),
        new( /* ShellMiddle */ 0.34, 0.26, 0.20, 7.0, true,  0.82),
        new( /* ShellAnchor */ 0.38, 0.20, 0.00, 0.0, true,  0.66),
        new( /* Sclera */     0.10, 0.08, 0.00, 0.0, false, 0.98),
        new( /* Iris */       0.52, 0.30, 0.00, 0.0, false, 1.00),
        new( /* Pupil */      0.03, 0.00, 0.00, 0.0, false, 1.00),
        new( /* Lid */        0.26, 0.10, 0.00, 0.0, false, 0.97),
    };

    public static MaterialShade Material(MaterialKind kind) =>
        Materials[(int)kind];

    // ---- mesh LOD ladder (the depth-pass Shed ladder maps here) ----

    /// <summary>
    /// Adaptive mesh LOD per quality tier: sphere subdivisions and ring segments
    /// reduce FIRST (task: the tiers reduce the cheap wins before the identity).
    /// The iris octagon never subdivides down — it is the signature.
    /// Undefined tiers fail closed to Minimal.
    /// </summary>
    public static MeshLod Lod(RenderQuality quality) => quality switch
    {
        RenderQuality.High => new MeshLod(
            SphereStacks: 10, SphereSlices: 14,
            OuterSegments: 6, OuterArcQuads: 8, OuterCross: 2,
            MidSegments: 3, MidArcQuads: 11, MidCross: 2,
            AnchorArcs: 3, AnchorArcQuads: 9, AnchorCross: 2,
            LidSlices: 9, LidBands: 2,
            DepthSlices: 20),
        RenderQuality.Medium => new MeshLod(
            SphereStacks: 8, SphereSlices: 12,
            OuterSegments: 6, OuterArcQuads: 6, OuterCross: 2,
            MidSegments: 3, MidArcQuads: 8, MidCross: 2,
            AnchorArcs: 3, AnchorArcQuads: 7, AnchorCross: 2,
            LidSlices: 7, LidBands: 2,
            DepthSlices: 14),
        RenderQuality.Low => new MeshLod(
            SphereStacks: 7, SphereSlices: 10,
            OuterSegments: 4, OuterArcQuads: 5, OuterCross: 2,
            MidSegments: 2, MidArcQuads: 6, MidCross: 2,
            AnchorArcs: 3, AnchorArcQuads: 4, AnchorCross: 2,
            LidSlices: 5, LidBands: 2,
            DepthSlices: 10),
        _ => new MeshLod(
            SphereStacks: 5, SphereSlices: 8,
            OuterSegments: 4, OuterArcQuads: 4, OuterCross: 1,
            MidSegments: 2, MidArcQuads: 4, MidCross: 1,
            AnchorArcs: 3, AnchorArcQuads: 3, AnchorCross: 1,
            LidSlices: 4, LidBands: 1,
            DepthSlices: 8),
    };
}

/// <summary>
/// One rung of the mesh LOD ladder. Quads along a ring arc, cross-section steps
/// through the band width, lat-long counts for the sphere, painter depth slices.
/// </summary>
/// <param name="SphereStacks">Latitude rows of the eye globe.</param>
/// <param name="SphereSlices">Longitude columns of the eye globe.</param>
/// <param name="OuterSegments">Arc segments of the far hull band.</param>
/// <param name="OuterArcQuads">Quads along each outer arc segment.</param>
/// <param name="OuterCross">Cross-section steps through the band width.</param>
/// <param name="MidSegments">Arc segments of the mid chassis.</param>
/// <param name="MidArcQuads">Quads along each mid arc segment.</param>
/// <param name="MidCross">Cross-section steps of the mid band.</param>
/// <param name="AnchorArcs">Wide arcs of the outer anchor cage.</param>
/// <param name="AnchorArcQuads">Quads along each anchor arc.</param>
/// <param name="AnchorCross">Cross-section steps of the anchor band.</param>
/// <param name="LidSlices">Longitude columns of each shutter lid.</param>
/// <param name="LidBands">Latitude bands of each shutter lid.</param>
/// <param name="DepthSlices">Painter's-algorithm depth slices.</param>
public sealed record MeshLod(
    int SphereStacks,
    int SphereSlices,
    int OuterSegments,
    int OuterArcQuads,
    int OuterCross,
    int MidSegments,
    int MidArcQuads,
    int MidCross,
    int AnchorArcs,
    int AnchorArcQuads,
    int AnchorCross,
    int LidSlices,
    int LidBands,
    int DepthSlices);
