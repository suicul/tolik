using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TOLIK.Desktop.Motion;
using TOLIK.Desktop.Performance;
using TOLIK.Desktop.Presence;

// The WindowsDesktop SDK (UseWindowsForms) adds implicit System.Drawing /
// System.Windows.Forms usings that clash with WPF types — pin WPF for this file.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace TOLIK.Desktop.Rendering;

/// <summary>
/// Draws one frame of the Cephalon from <see cref="CephalonParameters"/> + policy +
/// quality. The composition is a pure function of state: no identity, no memory,
/// no permissions, no network, no clock of its own — it renders what it is told.
///
/// Depth model (see <see cref="CephalonDepth"/>): the body reads as a volumetric
/// stack — reactor glow at the back (z = 0, the light source and parallax
/// anchor), hull band, mid chassis, near socket frame, eye assembly in front —
/// under a fixed upper-left key light. Perceived volume comes from: directional
/// gradient strokes (light side / core shadow side), a stacked glow falloff
/// (halo → breathing main → hot core), occlusion under-shadows where near layers
/// block the reactor light, a recessed eye socket (the eye sits IN the shell),
/// a sphere-shaded globe with matched speculars, gaze-driven micro-parallax
/// across planes, and depth-scaled motes. Every added draw call sheds first
/// under budget pressure (<see cref="CephalonDepth.Shed"/>); Minimal is the
/// flat Phase 1 composition.
///
/// Allocation discipline (AGENTS §43/§16): all brushes/pens — including every
/// depth gradient — are built once and frozen; gradient pens use relative
/// mapping so they resolve against geometry bounds at render time and never
/// need per-frame updates; arc/iris geometry fills reused buffers and reuses
/// StreamGeometry instances; particles live in a fixed struct pool; the only
/// per-frame text allocation is the debug overlay, rebuilt at most 4×/s.
/// </summary>
public sealed class CephalonComposition
{
    private const double DegToRad = Math.PI / 180.0;

    // Mote orbit radii (fractions of body radius); mote depth derives from these.
    private const double MoteRadiusMin = 0.84;
    private const double MoteRadiusMax = 1.16;

    // --- cached shell resources (tone-independent) ---
    private readonly Brush _brushGlobe;
    private readonly Brush _brushSocket;
    private readonly Brush _brushGlint;
    private readonly Brush _brushLidTop;
    private readonly Brush _brushLidBottom;
    private readonly Brush _brushMembrane;
    private readonly Brush _brushPupil;
    private readonly Brush _brushDebugBg;
    private readonly Brush _brushDebugText;
    private readonly Pen _scleraPen;
    private readonly Pen _scleraPenDepth;
    private readonly Pen _scleraInnerPen;
    private readonly Pen _scleraInnerPenDepth;
    private readonly Pen _shadowPen;
    private readonly TranslateTransform _shadowShift;
    private readonly Typeface _typeface = new("Consolas");

    // --- mutable geometry reused every frame (never frozen) ---
    private readonly StreamGeometry _outerGeo = new();
    private readonly StreamGeometry _middleGeo = new();
    private readonly StreamGeometry _bracketGeo = new();
    private readonly StreamGeometry _irisGeo = new();
    private readonly StreamGeometry _topLidGeo = new();
    private readonly StreamGeometry _bottomLidGeo = new();
    private readonly EllipseGeometry _eyeClip = new();
    private readonly ScaleTransform _tilt = new();

    private readonly GeoPoint[] _buffer = new GeoPoint[CephalonGeometry.MaxBufferPoints];
    private readonly Particle[] _particles = CreateParticles(28, seed: 0x5EEDC0DE7UL);

    // --- per-tone assets (iris/glow/pens follow the state tone) ---
    private readonly Dictionary<CephalonTone, ToneAssets> _toneAssets;

    // --- cached text ---
    private FormattedText? _caption;
    private string _captionKey = string.Empty;
    private FormattedText? _debugText;
    private double _lastDebugRefreshAt = double.NegativeInfinity;

    public CephalonComposition()
    {
        // Eye globe — a sphere, not a disc: lit pole toward the upper-left key
        // light, dark rim falling away into the socket.
        var globeStops = new GradientStopCollection
        {
            new GradientStop(WithAlpha(Lerp(CephalonPalette.ShellBase, CephalonPalette.EdgeDim, 0.38), 0.97), 0.0),
            new GradientStop(WithAlpha(CephalonPalette.ShellBase, 0.97), 0.45),
            new GradientStop(WithAlpha(Lerp(CephalonPalette.ShellBase, CephalonPalette.PupilDark, 0.55), 1.0), 1.0),
        };
        _brushGlobe = Freeze(new RadialGradientBrush(globeStops)
        {
            GradientOrigin = new Point(0.32, 0.30),
        });

        // Socket shadow — the eye sits IN the shell, not ON it: a recessed
        // annulus around the globe, deeper on the down-right side opposite the
        // key light. The eye disc occludes the hot center, leaving a visible
        // dark rim that reads as socket thickness.
        var socketStops = new GradientStopCollection
        {
            new GradientStop(WithAlpha(CephalonPalette.PupilDark, 0.55), 0.0),
            new GradientStop(WithAlpha(CephalonPalette.PupilDark, 0.45), 0.70),
            new GradientStop(WithAlpha(CephalonPalette.PupilDark, 0.0), 1.0),
        };
        _brushSocket = Freeze(new RadialGradientBrush(socketStops)
        {
            GradientOrigin = new Point(0.60, 0.62),
        });

        // Specular glint — a hot core with a soft falloff (glass, not paint).
        var glintStops = new GradientStopCollection
        {
            new GradientStop(WithAlpha(Colors.White, 0.95), 0.0),
            new GradientStop(WithAlpha(Colors.White, 0.30), 0.55),
            new GradientStop(WithAlpha(Colors.White, 0.0), 1.0),
        };
        _brushGlint = Freeze(new RadialGradientBrush(glintStops));

        // Shutter membranes — turned edges: the key light falls from up-left, so
        // the top lid darkens toward its closing edge (down) and the bottom lid
        // toward its own (up). A closed eye reads as a recessed slot, not a
        // flat plate.
        var lidTopStops = new GradientStopCollection
        {
            new GradientStop(WithAlpha(Lerp(CephalonPalette.ShellBase, CephalonPalette.EdgeDim, 0.22), 0.97), 0.0),
            new GradientStop(WithAlpha(Sink(CephalonPalette.ShellBase, 0.50), 0.97), 1.0),
        };
        _brushLidTop = Freeze(new LinearGradientBrush(lidTopStops, new Point(0.0, 0.0), new Point(0.0, 1.0)));

        var lidBottomStops = new GradientStopCollection
        {
            new GradientStop(WithAlpha(Sink(CephalonPalette.ShellBase, 0.50), 0.97), 0.0),
            new GradientStop(WithAlpha(Lerp(CephalonPalette.ShellBase, CephalonPalette.EdgeDim, 0.22), 0.97), 1.0),
        };
        _brushLidBottom = Freeze(new LinearGradientBrush(lidBottomStops, new Point(0.0, 0.0), new Point(0.0, 1.0)));

        _brushMembrane = Freeze(new SolidColorBrush(WithAlpha(CephalonPalette.ShellBase, 0.97)));
        _brushPupil = Freeze(new SolidColorBrush(CephalonPalette.PupilDark));
        _brushDebugBg = Freeze(new SolidColorBrush(WithAlpha(Colors.Black, 0.62)));
        _brushDebugText = Freeze(new SolidColorBrush(WithAlpha(CephalonPalette.TextDim, 0.95)));
        _scleraPen = Freeze(MakePen(WithAlpha(CephalonPalette.ShellEdge, 0.85), 1.2));
        _scleraInnerPen = Freeze(MakePen(WithAlpha(CephalonPalette.ShellEdge, 0.45), 1.0));
        _scleraPenDepth = MakeDirectionalPen(
            WithAlpha(Lerp(CephalonPalette.ShellEdge, CephalonPalette.EdgeDim, 0.55), 0.95),
            WithAlpha(Sink(CephalonPalette.ShellEdge, 0.45), 0.85),
            1.2);
        _scleraInnerPenDepth = MakeDirectionalPen(
            WithAlpha(Lerp(CephalonPalette.ShellEdge, CephalonPalette.EdgeDim, 0.40), 0.60),
            WithAlpha(Sink(CephalonPalette.ShellEdge, 0.50), 0.32),
            1.0);
        _shadowPen = Freeze(MakePen(WithAlpha(Lerp(CephalonPalette.ShellBase, CephalonPalette.PupilDark, 0.60), 0.32), 2.6));

        // Under-shadow cast by near layers onto the reactor light behind them:
        // a constant down-right offset, opposite the key light. Frozen once.
        _shadowShift = Freeze(new TranslateTransform(1.2, 1.5));

        _toneAssets = new Dictionary<CephalonTone, ToneAssets>();
        foreach (CephalonTone tone in Enum.GetValues<CephalonTone>())
        {
            _toneAssets[tone] = BuildToneAssets(tone);
        }
    }

    /// <summary>
    /// Renders one frame. <paramref name="measuredFps"/> and the hotkey line exist
    /// only for the honest diagnostics overlay.
    /// </summary>
    public void Render(
        DrawingContext dc,
        double width,
        double height,
        CephalonAnimator animator,
        ModePolicy policy,
        RenderQuality quality,
        double pixelsPerDip,
        bool showDebug,
        string? debugHotkeyLine,
        double measuredFps,
        double nowSeconds)
    {
        CephalonParameters p = animator.Parameters;
        double size = Math.Min(width, height);
        if (size <= 1.0)
        {
            return;
        }

        double cx = width / 2.0;
        double cy = height * 0.47;
        double r = CephalonGeometry.BaseRadius(size);
        double eyeR = r * 0.52;
        double time = animator.TimeSeconds;
        ToneAssets assets = GetAssets(animator.Traits.Tone);
        DepthShedding shed = CephalonDepth.Shed(quality);

        // ---- depth planes (CephalonDepth): every layer translates with the
        // gaze at a rate proportional to its nearness — the stereo hint, driven
        // by the animator's own pupil axis. No new drive axis exists or is
        // needed. The glow core (z = 0) is the anchor and never slides.
        double gazeX = Math.Clamp(p.PupilOffsetX, -1.0, 1.0);
        double gazeY = Math.Clamp(p.PupilOffsetY, -1.0, 1.0);
        double oX = CephalonDepth.Parallax(CephalonDepth.ZOuterRing, gazeX, eyeR);
        double oY = CephalonDepth.Parallax(CephalonDepth.ZOuterRing, gazeY, eyeR);
        double mX = CephalonDepth.Parallax(CephalonDepth.ZMiddleArcs, gazeX, eyeR);
        double mY = CephalonDepth.Parallax(CephalonDepth.ZMiddleArcs, gazeY, eyeR);
        double bX = CephalonDepth.Parallax(CephalonDepth.ZBrackets, gazeX, eyeR);
        double bY = CephalonDepth.Parallax(CephalonDepth.ZBrackets, gazeY, eyeR);
        double sX = CephalonDepth.Parallax(CephalonDepth.ZSpokes, gazeX, eyeR);
        double sY = CephalonDepth.Parallax(CephalonDepth.ZSpokes, gazeY, eyeR);
        double eX = CephalonDepth.Parallax(CephalonDepth.ZEye, gazeX, eyeR);
        double eY = CephalonDepth.Parallax(CephalonDepth.ZEye, gazeY, eyeR);
        double eCx = cx + eX;
        double eCy = cy + eY;

        // ---- body (everything inside the silhouette tilt) ----
        _tilt.ScaleX = 1.0 + p.ShellTilt;
        _tilt.ScaleY = 1.0 - p.ShellTilt;
        _tilt.CenterX = cx;
        _tilt.CenterY = cy;
        dc.PushTransform(_tilt);

        // 1) reactor glow stack (z = 0 — the light source): a wide atmospheric
        //    halo, the breathing main glow, and a hot core that the socket frame
        //    and eye occlude — light with a falloff IN the round, not a flat
        //    disc. Voice spectrum (CE-3 axis) shimmers the whole stack equally —
        //    exactly zero while silent.
        dc.PushOpacity(0.30 + (0.55 * p.GlowIntensity) + (0.10 * p.SpectrumIntensity));
        double glowR = r * (1.55 + 0.35 * p.GlowIntensity) * p.ShellExpansion;
        if (shed.AtmosphericHalo)
        {
            double haloR = r * 1.95 * p.ShellExpansion;
            dc.DrawEllipse(assets.GlowWide, null, new Point(cx, cy), haloR, haloR);
        }
        dc.DrawEllipse(assets.Glow, null, new Point(cx, cy), glowR, glowR);
        if (shed.HotCore)
        {
            double coreR = r * 0.85 * p.ShellExpansion;
            dc.DrawEllipse(assets.GlowCore, null, new Point(cx, cy), coreR, coreR);
        }
        dc.Pop();

        // 2) outer segmented ring (far hull band; slow direct rotation, deformable)
        BuildRing(_outerGeo, cx + oX, cy + oY, r * 1.06 * p.ShellExpansion, segments: 6,
            spanDeg: 40, gapDeg: 20, rotation: p.ShellRotation,
            wobbleAmp: p.WobbleAmp * 0.6, wobblePhase: p.WobblePhase,
            harmonics: animator.WobbleHarmonics, pointsPerArc: PointsPerArc(quality));
        dc.DrawGeometry(null, shed.DirectionalShading ? assets.OuterPenDepth : assets.OuterPen, _outerGeo);

        // 3) middle arcs (mid chassis, counter-rotation, full wobble — the
        //    deformable shell); casts a soft under-shadow onto the reactor light
        BuildRing(_middleGeo, cx + mX, cy + mY, r * 0.90 * p.ShellExpansion, segments: 3,
            spanDeg: 78, gapDeg: 42, rotation: p.Shell2Rotation,
            wobbleAmp: p.WobbleAmp, wobblePhase: (p.WobblePhase * 1.7) + 1.3,
            harmonics: animator.WobbleHarmonics, pointsPerArc: PointsPerArc(quality));
        if (shed.OcclusionShadows)
        {
            dc.PushTransform(_shadowShift);
            dc.DrawGeometry(null, _shadowPen, _middleGeo);
            dc.Pop();
        }
        dc.DrawGeometry(null, shed.DirectionalShading ? assets.MiddlePenDepth : assets.MiddlePen, _middleGeo);

        // 4) inner brackets (near socket frame; crisp counterpoint, no wobble) —
        //    floats in front of the chassis: under-shadow + sharpest light/shadow split
        BuildRing(_bracketGeo, cx + bX, cy + bY, r * 0.70, segments: 4,
            spanDeg: 22, gapDeg: 68, rotation: p.BracketRotation,
            wobbleAmp: 0.0, wobblePhase: 0.0, harmonics: 1,
            pointsPerArc: Math.Max(6, PointsPerArc(quality) / 2));
        if (shed.OcclusionShadows)
        {
            dc.PushTransform(_shadowShift);
            dc.DrawGeometry(null, _shadowPen, _bracketGeo);
            dc.Pop();
        }
        dc.DrawGeometry(null, shed.DirectionalShading ? assets.BracketPenDepth : assets.BracketPen, _bracketGeo);

        // 5) energy spokes between the socket frame and the chassis
        int spokes = SpokeCount(quality);
        dc.PushOpacity(0.15 + 0.55 * p.GlowIntensity);
        for (int i = 0; i < spokes; i++)
        {
            double a = (p.BracketRotation * 0.5) + (CephalonGeometry.Tau * i / spokes);
            GeoPoint from = CephalonGeometry.Polar(cx + sX, cy + sY, r * 0.74, a);
            GeoPoint to = CephalonGeometry.Polar(cx + sX, cy + sY, r * 0.84, a);
            dc.DrawLine(assets.SpokePen, Pt(from), Pt(to));
        }
        dc.Pop();

        // 6) eye socket — the eye sits IN the shell: a recessed annular shadow
        //    around the globe, deeper opposite the key light
        if (shed.OcclusionShadows)
        {
            double sockR = eyeR * 1.26;
            dc.DrawEllipse(_brushSocket, null, new Point(eCx, eCy), sockR, sockR);
        }

        // 7) eye globe — sphere shading + directional sclera rims
        dc.DrawEllipse(_brushGlobe, shed.DirectionalShading ? _scleraPenDepth : _scleraPen,
            new Point(eCx, eCy), eyeR, eyeR);
        dc.DrawEllipse(null, shed.DirectionalShading ? _scleraInnerPenDepth : _scleraInnerPen,
            new Point(eCx, eCy), eyeR * 0.86, eyeR * 0.86);

        // 8) angular iris — TOLIK's own facet signature (octagon, slowly
        //    counter-tilting) + a glassy sheen on the key-light side
        double irisR = eyeR * 0.66;
        double pupilR = eyeR * 0.30 * p.PupilScale;
        int facets = CephalonGeometry.FillFacetPolygon(_buffer, eCx, eCy, irisR, 8, p.IrisRotation);
        BuildPolygon(_irisGeo, facets);
        dc.DrawGeometry(assets.Iris, null, _irisGeo);
        for (int i = 0; i < facets; i++)
        {
            double a = p.IrisRotation + (CephalonGeometry.Tau * i / facets);
            dc.DrawLine(assets.FacetPen,
                Pt(CephalonGeometry.Polar(eCx, eCy, pupilR * 0.9, a)),
                Pt(CephalonGeometry.Polar(eCx, eCy, irisR * 0.94, a)));
        }
        if (shed.DirectionalShading)
        {
            dc.DrawGeometry(assets.IrisSheen, null, _irisGeo);
        }

        // 9) pupil (dilated pupil travels less — physical, and keeps it inside
        //    the iris); travels within the shifted eye plane
        double travel = eyeR * 0.30 / Math.Max(1.0, p.PupilScale);
        double px = eCx + (p.PupilOffsetX * travel);
        double py = eCy + (p.PupilOffsetY * travel);
        dc.DrawEllipse(_brushPupil, assets.PupilRimPen, new Point(px, py), pupilR, pupilR);

        // 10) speculars — a hot key-light glint up-left of the pupil, plus a
        //     faint bounce light low-right (DirectionalShading tier): the eye
        //     reads as wet glass under one consistent light
        dc.PushOpacity(0.4);
        dc.DrawEllipse(_brushGlint, null,
            new Point(px - (pupilR * 0.34), py - (pupilR * 0.36)), pupilR * 0.22, pupilR * 0.22);
        dc.Pop();
        if (shed.DirectionalShading)
        {
            dc.PushOpacity(0.28);
            dc.DrawEllipse(assets.Particle, null,
                new Point(px + (pupilR * 0.38), py + (pupilR * 0.34)), pupilR * 0.12, pupilR * 0.12);
            dc.Pop();
        }

        // 11) mechanical shutter eyelids
        if (p.EyeOpen < 0.995)
        {
            DrawEyelids(dc, eCx, eCy, eyeR, p.EyeOpen, assets, shed.DirectionalShading);
        }

        // 12) energy particles (ambient motes between shell layers) — size and
        //     brightness follow their depth plane; the swarm parallaxes with the gaze
        DrawParticles(dc, cx, cy, r, time, p.ParticleEnergy, p.SpectrumIntensity,
            policy, quality, assets, gazeX, gazeY, eyeR);

        dc.Pop(); // tilt

        // ---- captions & diagnostics (outside the tilt) ----
        if (policy.ShowCaption)
        {
            DrawCaption(dc, width, height, r, pixelsPerDip, assets);
        }

        if (showDebug && policy.AllowDebugOverlay)
        {
            DrawDebugOverlay(dc, pixelsPerDip, policy, quality, debugHotkeyLine, measuredFps, nowSeconds);
        }
    }

    // ---------- geometry builders ----------

    private void BuildRing(
        StreamGeometry geo, double cx, double cy, double radius,
        int segments, double spanDeg, double gapDeg, double rotation,
        double wobbleAmp, double wobblePhase, int harmonics, int pointsPerArc)
    {
        using StreamGeometryContext ctx = geo.Open();
        double step = (spanDeg + gapDeg) * DegToRad;
        double span = spanDeg * DegToRad;
        for (int s = 0; s < segments; s++)
        {
            double a0 = rotation + (s * step);
            int n = CephalonGeometry.FillArcPoints(
                _buffer, cx, cy, radius, a0, span, pointsPerArc, wobbleAmp, wobblePhase, harmonics);
            ctx.BeginFigure(Pt(_buffer[0]), isFilled: false, isClosed: false);
            for (int i = 1; i < n; i++)
            {
                ctx.LineTo(Pt(_buffer[i]), isStroked: true, isSmoothJoin: false);
            }
        }
    }

    private void BuildPolygon(StreamGeometry geo, int pointCount)
    {
        using StreamGeometryContext ctx = geo.Open();
        if (pointCount <= 0)
        {
            return;
        }
        ctx.BeginFigure(Pt(_buffer[0]), isFilled: true, isClosed: true);
        for (int i = 1; i < pointCount; i++)
        {
            ctx.LineTo(Pt(_buffer[i]), isStroked: true, isSmoothJoin: false);
        }
    }

    private void DrawEyelids(DrawingContext dc, double cx, double cy, double eyeR, double open, ToneAssets assets, bool directional)
    {
        double closePerSide = (1.0 - open) * eyeR;
        double topY = cy - eyeR + closePerSide;
        double bottomY = cy + eyeR - closePerSide;
        double x0 = cx - eyeR - 4;
        double x1 = cx + eyeR + 4;

        _eyeClip.Center = new Point(cx, cy);
        _eyeClip.RadiusX = eyeR + 1;
        _eyeClip.RadiusY = eyeR + 1;

        dc.PushClip(_eyeClip);

        using (StreamGeometryContext ctx = _topLidGeo.Open())
        {
            ctx.BeginFigure(new Point(x0, cy - eyeR - 6), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(x1, cy - eyeR - 6), true, false);
            ctx.LineTo(new Point(x1, topY), true, false);
            ctx.LineTo(new Point(x0, topY), true, false);
        }
        dc.DrawGeometry(directional ? _brushLidTop : _brushMembrane, null, _topLidGeo);
        dc.DrawLine(assets.MembraneEdgePen, new Point(x0, topY), new Point(x1, topY));

        using (StreamGeometryContext ctx = _bottomLidGeo.Open())
        {
            ctx.BeginFigure(new Point(x0, cy + eyeR + 6), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(x1, cy + eyeR + 6), true, false);
            ctx.LineTo(new Point(x1, bottomY), true, false);
            ctx.LineTo(new Point(x0, bottomY), true, false);
        }
        dc.DrawGeometry(directional ? _brushLidBottom : _brushMembrane, null, _bottomLidGeo);
        dc.DrawLine(assets.MembraneEdgePen, new Point(x0, bottomY), new Point(x1, bottomY));

        dc.Pop(); // clip
    }

    private void DrawParticles(
        DrawingContext dc, double cx, double cy, double r, double time,
        double energy, double spectrum, ModePolicy policy, RenderQuality quality,
        ToneAssets assets, double gazeX, double gazeY, double eyeR)
    {
        if (!policy.AllowParticles)
        {
            return;
        }

        int max = ParticleMax(quality);
        if (max <= 0)
        {
            return;
        }

        int count = Math.Min((int)Math.Ceiling(max * Math.Clamp(energy, 0.0, 1.0)), _particles.Length);
        for (int i = 0; i < count; i++)
        {
            ref readonly Particle q = ref _particles[i];
            // Voice spectrum quickens the motes (CE-3 axis) — exactly zero while silent.
            double speed = q.Speed * (0.6 + (0.8 * energy) + (0.6 * Math.Clamp(spectrum, 0.0, 1.0)));
            double a = q.Angle + (speed * time);
            double radius = r * q.Radius * (1.0 + (0.02 * Math.Sin((time * q.Speed * 3.0) + q.Phase)));

            // Depth plane of this mote: inner orbits read NEARER — brighter and
            // larger (atmospheric perspective) — and parallax more with the gaze,
            // so the swarm separates into layers when the eye moves.
            double bright = CephalonDepth.Atmosphere(q.Depth);
            double alpha = Math.Clamp(
                (0.25 + (0.75 * energy)) * (0.55 + (0.45 * Math.Sin((time * 2.2) + q.Phase))) * bright,
                0.0, 1.0);
            double size = q.Size * (0.80 + (0.35 * q.Depth));

            double mX = cx + CephalonDepth.Parallax(q.Depth, gazeX, eyeR);
            double mY = cy + CephalonDepth.Parallax(q.Depth, gazeY, eyeR);
            GeoPoint head = CephalonGeometry.Polar(mX, mY, radius, a);
            GeoPoint tail = CephalonGeometry.Polar(mX, mY, radius, a - (0.10 * Math.Sign(q.Speed)));

            dc.PushOpacity(alpha);
            dc.DrawEllipse(assets.Particle, null, Pt(head), size, size);
            dc.DrawEllipse(assets.Particle, null, Pt(tail), size * 0.6, size * 0.6);
            dc.Pop();
        }
    }

    // ---------- text ----------

    private void DrawCaption(DrawingContext dc, double width, double height, double r, double pixelsPerDip, ToneAssets assets)
    {
        string key = $"{ModeName}:{width:F0}x{height:F0}:{pixelsPerDip:F2}";
        if (_caption is null || _captionKey != key)
        {
            _caption = new FormattedText(
                "TOLIK · " + ModeName,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _typeface,
                10.5,
                assets.Caption,
                pixelsPerDip);
            _caption.TextAlignment = TextAlignment.Center;
            _captionKey = key;
        }

        double y = Math.Min(height - 18, (height * 0.47) + (r * 1.30));
        dc.DrawText(_caption, new Point(width / 2.0, y));
    }

    private void DrawDebugOverlay(
        DrawingContext dc, double pixelsPerDip, ModePolicy policy, RenderQuality quality,
        string? hotkeyLine, double measuredFps, double nowSeconds)
    {
        if (nowSeconds - _lastDebugRefreshAt >= 0.25 || _debugText is null)
        {
            string text =
                $"fps      {measuredFps,5:0.0} / {policy.TargetFps:0}\n" +
                $"frame    {EwmaMsText}\n" +
                $"quality  {quality}\n" +
                $"mode     {ModeName}\n" +
                $"core     {CoreLine}";
            if (!string.IsNullOrEmpty(ExpressionLine))
            {
                text += "\nexpr     " + ExpressionLine;
            }
            if (!string.IsNullOrEmpty(ReasonLine))
            {
                text += "\nreason   " + ReasonLine;
            }
            if (!string.IsNullOrEmpty(hotkeyLine))
            {
                text += "\n" + hotkeyLine;
            }

            _debugText = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _typeface,
                11.5,
                _brushDebugText,
                pixelsPerDip);
            _lastDebugRefreshAt = nowSeconds;
        }

        double pad = 8.0;
        var rect = new Rect(pad / 2.0, pad / 2.0, _debugText.Width + pad, _debugText.Height + pad);
        dc.DrawRectangle(_brushDebugBg, null, rect);
        dc.DrawText(_debugText, new Point(pad, pad / 2.0));
    }

    private string EwmaMsText => GovernorFrameMs >= 0 ? $"{GovernorFrameMs:0.0} ms" : "n/a";

    /// <summary>Latest governor EWMA, injected by the host right before rendering.</summary>
    public double GovernorFrameMs { get; set; } = -1.0;

    /// <summary>Current mode name, set by the host for caption/debug text.</summary>
    public string ModeName { get; set; } = "Normal";

    /// <summary>Connection truth (CE-2), set by the host from the Core link presenter.
    /// The default preserves the exact Phase 1 line for an unlinked host.</summary>
    public string CoreLine { get; set; } = "local, no Core connection";

    /// <summary>Expression truth (what the body is doing right now); null = hidden.
    /// Phase 1 default: hidden (no Core expression stream existed).</summary>
    public string? ExpressionLine { get; set; }

    /// <summary>Token-free one-line reason for offline/auth-rejected states; null = hidden.</summary>
    public string? ReasonLine { get; set; }

    // ---------- quality mapping ----------

    private static int PointsPerArc(RenderQuality q) => q switch
    {
        RenderQuality.High => 26,
        RenderQuality.Medium => 18,
        RenderQuality.Low => 12,
        _ => 8,
    };

    private static int SpokeCount(RenderQuality q) => q switch
    {
        RenderQuality.High => 12,
        RenderQuality.Medium => 8,
        RenderQuality.Low => 6,
        _ => 4,
    };

    private static int ParticleMax(RenderQuality q) => q switch
    {
        RenderQuality.High => 28,
        RenderQuality.Medium => 14,
        RenderQuality.Low => 6,
        _ => 0,
    };

    // ---------- resources ----------

    private ToneAssets GetAssets(CephalonTone tone) =>
        _toneAssets.TryGetValue(tone, out ToneAssets? assets) ? assets : _toneAssets[CephalonTone.Calm];

    private static ToneAssets BuildToneAssets(CephalonTone tone)
    {
        (Color iris, Color accent) = CephalonPalette.Resolve(tone);

        var irisStops = new GradientStopCollection
        {
            new GradientStop(WithAlpha(CephalonPalette.IrisBright, 0.95), 0.0),
            new GradientStop(WithAlpha(iris, 0.85), 0.55),
            new GradientStop(WithAlpha(CephalonPalette.IrisDeep, 0.95), 1.0),
        };
        var irisBrush = new RadialGradientBrush(irisStops);

        var glowStops = new GradientStopCollection
        {
            new GradientStop(WithAlpha(accent, 0.40), 0.0),
            new GradientStop(WithAlpha(accent, 0.10), 0.55),
            new GradientStop(WithAlpha(accent, 0.0), 1.0),
        };
        var glowBrush = new RadialGradientBrush(glowStops);

        // Volumetric glow stack: a wide atmospheric halo enveloping the whole
        // body, and a hot reactor core behind the eye — the socket frame and
        // the globe occlude it, so the light reads as coming from INSIDE.
        var glowWideStops = new GradientStopCollection
        {
            new GradientStop(WithAlpha(accent, 0.12), 0.0),
            new GradientStop(WithAlpha(accent, 0.05), 0.55),
            new GradientStop(WithAlpha(accent, 0.0), 1.0),
        };
        var glowCoreStops = new GradientStopCollection
        {
            new GradientStop(WithAlpha(Lift(accent, 0.35), 0.42), 0.0),
            new GradientStop(WithAlpha(accent, 0.16), 0.50),
            new GradientStop(WithAlpha(accent, 0.0), 1.0),
        };

        // Directional strokes: key light up-left → core shadow down-right. The
        // far hull band keeps the flattest contrast; the near socket frame gets
        // the sharpest light/shadow split (atmospheric perspective in the strokes).
        // Mean per-ring energy stays at the Phase 1 alphas — only direction was added.
        var outerPenDepth = MakeDirectionalPen(
            WithAlpha(Lift(accent, 0.22), 0.92), WithAlpha(Sink(accent, 0.40), 0.70), 2.2);
        var middlePenDepth = MakeDirectionalPen(
            WithAlpha(Lift(accent, 0.30), 0.72), WithAlpha(Sink(accent, 0.45), 0.40), 1.5);
        var bracketPenDepth = MakeDirectionalPen(
            WithAlpha(Lift(accent, 0.45), 0.62), WithAlpha(Sink(accent, 0.55), 0.24), 1.1);

        // Glassy sheen over the iris facets, key-light side only.
        var sheenStops = new GradientStopCollection
        {
            new GradientStop(WithAlpha(CephalonPalette.IrisBright, 0.16), 0.0),
            new GradientStop(WithAlpha(CephalonPalette.IrisBright, 0.0), 0.55),
        };
        var sheenBrush = new LinearGradientBrush(sheenStops, new Point(0.0, 0.0), new Point(1.0, 1.0));

        return new ToneAssets(
            Iris: Freeze(irisBrush),
            Glow: Freeze(glowBrush),
            GlowWide: Freeze(new RadialGradientBrush(glowWideStops)),
            GlowCore: Freeze(new RadialGradientBrush(glowCoreStops)),
            IrisSheen: Freeze(sheenBrush),
            Particle: Freeze(new SolidColorBrush(WithAlpha(accent, 0.85))),
            Caption: Freeze(new SolidColorBrush(WithAlpha(accent, 0.80))),
            OuterPen: Freeze(MakePen(WithAlpha(accent, 0.85), 2.2)),
            MiddlePen: Freeze(MakePen(WithAlpha(accent, 0.55), 1.5)),
            BracketPen: Freeze(MakePen(WithAlpha(accent, 0.40), 1.1)),
            OuterPenDepth: outerPenDepth,
            MiddlePenDepth: middlePenDepth,
            BracketPenDepth: bracketPenDepth,
            SpokePen: Freeze(MakePen(WithAlpha(accent, 0.50), 1.0)),
            FacetPen: Freeze(MakePen(WithAlpha(accent, 0.30), 1.0)),
            PupilRimPen: Freeze(MakePen(WithAlpha(accent, 0.55), 1.0)),
            MembraneEdgePen: Freeze(MakePen(WithAlpha(accent, 0.45), 1.2)));
    }

    private static Color WithAlpha(Color c, double opacity)
    {
        byte a = (byte)Math.Clamp((int)Math.Round(opacity * 255.0), 0, 255);
        return Color.FromArgb(a, c.R, c.G, c.B);
    }

    /// <summary>Linear blend between two palette colors (t clamped to [0,1]);
    /// used only to derive depth variants of existing palette entries.</summary>
    private static Color Lerp(Color a, Color b, double t)
    {
        double k = Math.Clamp(t, 0.0, 1.0);
        return Color.FromArgb(
            a.A,
            (byte)Math.Round(a.R + ((b.R - a.R) * k)),
            (byte)Math.Round(a.G + ((b.G - a.G) * k)),
            (byte)Math.Round(a.B + ((b.B - a.B) * k)));
    }

    /// <summary>Lift a color toward white (specular side).</summary>
    private static Color Lift(Color c, double t) => Lerp(c, Colors.White, t);

    /// <summary>Sink a color toward the pupil dark (core-shadow side).</summary>
    private static Color Sink(Color c, double t) => Lerp(c, CephalonPalette.PupilDark, t);

    /// <summary>
    /// A stroke pen whose brush runs light → shadow along the fixed key-light
    /// axis (upper-left → lower-right). Relative mapping resolves against the
    /// stroked geometry's bounds at render time, so the gradient stays in WORLD
    /// space while the arcs rotate under it — the light never spins with the
    /// body — and the frozen brush needs zero per-frame updates.
    /// </summary>
    private static Pen MakeDirectionalPen(Color light, Color shadow, double thickness)
    {
        var stops = new GradientStopCollection
        {
            new GradientStop(light, 0.0),
            new GradientStop(shadow, 1.0),
        };
        var brush = new LinearGradientBrush(stops, new Point(0.0, 0.0), new Point(1.0, 1.0))
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
        };
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static Pen MakePen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static Point Pt(GeoPoint p) => new(p.X, p.Y);

    private readonly struct Particle
    {
        public Particle(double Angle, double Radius, double Speed, double Size, double Phase, double Depth)
        {
            this.Angle = Angle;
            this.Radius = Radius;
            this.Speed = Speed;
            this.Size = Size;
            this.Phase = Phase;
            this.Depth = Depth;
        }

        public double Angle { get; }
        public double Radius { get; }
        public double Speed { get; }
        public double Size { get; }
        public double Phase { get; }

        /// <summary>Depth plane of the mote: inner orbits read nearer ([0,1]).</summary>
        public double Depth { get; }
    }

    private static Particle[] CreateParticles(int count, ulong seed)
    {
        var rand = new XorShiftRandom(seed);
        var particles = new Particle[count];
        for (int i = 0; i < count; i++)
        {
            double radius = rand.Next(MoteRadiusMin, MoteRadiusMax);
            particles[i] = new Particle(
                Angle: (CephalonGeometry.Tau * i / count) + rand.Next(-0.2, 0.2),
                Radius: radius,
                Speed: rand.Next(0.12, 0.5) * (i % 2 == 0 ? 1 : -1),
                Size: rand.Next(1.0, 2.4),
                Phase: rand.Next(0.0, CephalonGeometry.Tau),
                Depth: (MoteRadiusMax - radius) / (MoteRadiusMax - MoteRadiusMin));
        }
        return particles;
    }

    private sealed record ToneAssets(
        Brush Iris,
        Brush Glow,
        Brush GlowWide,
        Brush GlowCore,
        Brush IrisSheen,
        Brush Particle,
        Brush Caption,
        Pen OuterPen,
        Pen MiddlePen,
        Pen BracketPen,
        Pen OuterPenDepth,
        Pen MiddlePenDepth,
        Pen BracketPenDepth,
        Pen SpokePen,
        Pen FacetPen,
        Pen PupilRimPen,
        Pen MembraneEdgePen);
}
