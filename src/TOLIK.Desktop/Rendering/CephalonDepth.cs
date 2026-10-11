using TOLIK.Desktop.Performance;

namespace TOLIK.Desktop.Rendering;

/// <summary>
/// Depth model of the Cephalon body — pure, framework-free math. The same
/// numbers drive a 3D/VR composition later: each plane maps to a z-layer, the
/// parallax becomes a real camera, the atmosphere curve becomes fog (docs/VR.md).
///
/// The depth story: the reactor glow is the light source at the BACK of the
/// machine (z = 0, the anchor that never slides); the outer ring is the far
/// hull band; the middle arcs are the mid chassis; the brackets are the near
/// socket frame; the eye assembly is the nearest plane. A fixed key light comes
/// from the upper-left — every directional gradient in the renderer follows
/// that single axis.
/// </summary>
public static class CephalonDepth
{
    public const double ZMin = 0.0;
    public const double ZMax = 1.0;

    public const double ZGlowCore = 0.0;    // reactor light — the anchor plane
    public const double ZOuterRing = 0.30;  // far hull band
    public const double ZMiddleArcs = 0.52; // mid chassis
    public const double ZSpokes = 0.62;     // energy traces on the mid structure
    public const double ZBrackets = 0.74;   // near socket frame
    public const double ZEye = 1.0;         // eye assembly — the nearest plane

    /// <summary>Max parallax shift of the nearest plane, as a fraction of the
    /// eye radius per unit of gaze signal. A stereo hint, not a jiggle: at the
    /// default 380px window the eye plane slides ≈4px at full gaze.</summary>
    public const double ParallaxStrength = 0.055;

    /// <summary>Brightness floor of the farthest plane (atmospheric perspective:
    /// far things dim toward the hull shadow).</summary>
    public const double AtmosphereFloor = 0.60;

    /// <summary>
    /// Per-plane gaze parallax in pixels: near planes translate more — the
    /// stereo depth hint. <paramref name="signal"/> is the animator's existing
    /// pupil-gaze axis ([-1,1]); <paramref name="unit"/> is the eye radius in
    /// pixels. Non-finite input fails closed to zero shift.
    /// </summary>
    public static double Parallax(double z, double signal, double unit)
    {
        if (!double.IsFinite(z) || !double.IsFinite(signal) || !double.IsFinite(unit))
        {
            return 0.0;
        }

        double depth = Math.Clamp(z, ZMin, ZMax);
        double s = Math.Clamp(signal, -1.0, 1.0);
        double u = Math.Max(unit, 0.0);
        return ParallaxStrength * depth * s * u;
    }

    /// <summary>
    /// Atmospheric brightness factor of a plane: <see cref="AtmosphereFloor"/>
    /// at the farthest depth rising linearly to 1.0 at the nearest. Monotone in
    /// z; non-finite z fails closed to the floor.
    /// </summary>
    public static double Atmosphere(double z)
    {
        if (!double.IsFinite(z))
        {
            return AtmosphereFloor;
        }

        double depth = Math.Clamp(z, ZMin, ZMax);
        return AtmosphereFloor + ((1.0 - AtmosphereFloor) * depth);
    }

    /// <summary>
    /// Which depth effects survive at a quality tier — the shedding ladder.
    /// Expensive volumetric work sheds FIRST: the atmospheric halo goes before
    /// occlusion shadows, occlusion before the hot core; directional shading —
    /// the soul of the depth read — survives longest. Minimal (and any
    /// undefined tier, fail-closed) is the flat Phase 1 composition.
    /// </summary>
    public static DepthShedding Shed(RenderQuality quality) => quality switch
    {
        RenderQuality.High => DepthShedding.All,
        RenderQuality.Medium => new DepthShedding(
            AtmosphericHalo: false, HotCore: true, OcclusionShadows: true, DirectionalShading: true),
        RenderQuality.Low => new DepthShedding(
            AtmosphericHalo: false, HotCore: false, OcclusionShadows: false, DirectionalShading: true),
        _ => DepthShedding.None,
    };
}

/// <summary>
/// The set of depth effects enabled at one quality tier. Flags are ordered so
/// the shedding ladder only ever removes work: every tier is a subset of the
/// tier above it (proven by tests via <see cref="Contains"/>).
/// </summary>
/// <param name="AtmosphericHalo">Wide faint halo layer around the glow stack.</param>
/// <param name="HotCore">Hot reactor core disc behind the eye.</param>
/// <param name="OcclusionShadows">Socket recess + under-shadows of near layers.</param>
/// <param name="DirectionalShading">Gradient strokes, iris sheen, bounce glint, gradient membranes.</param>
public readonly record struct DepthShedding(
    bool AtmosphericHalo,
    bool HotCore,
    bool OcclusionShadows,
    bool DirectionalShading)
{
    public static DepthShedding None => new(false, false, false, false);

    public static DepthShedding All => new(true, true, true, true);

    /// <summary>True when every effect enabled in <paramref name="other"/> is
    /// also enabled here (subset check).</summary>
    public bool Contains(DepthShedding other) =>
        (!other.AtmosphericHalo || AtmosphericHalo) &&
        (!other.HotCore || HotCore) &&
        (!other.OcclusionShadows || OcclusionShadows) &&
        (!other.DirectionalShading || DirectionalShading);
}
