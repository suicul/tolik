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
/// Allocation discipline (AGENTS §43/§16): all brushes/pens are built once and
/// frozen; arc/iris geometry fills reused buffers and reuses StreamGeometry
/// instances; particles live in a fixed struct pool; the only per-frame text
/// allocation is the debug overlay, rebuilt at most 4×/s.
/// </summary>
public sealed class CephalonComposition
{
    private const double DegToRad = Math.PI / 180.0;

    // --- cached shell resources (tone-independent) ---
    private readonly Brush _brushShell;
    private readonly Brush _brushMembrane;
    private readonly Brush _brushPupil;
    private readonly Brush _brushReflection;
    private readonly Brush _brushDebugBg;
    private readonly Brush _brushDebugText;
    private readonly Pen _scleraPen;
    private readonly Pen _scleraInnerPen;
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
        _brushShell = Freeze(new SolidColorBrush(WithAlpha(CephalonPalette.ShellBase, 0.94)));
        _brushMembrane = Freeze(new SolidColorBrush(WithAlpha(CephalonPalette.ShellBase, 0.97)));
        _brushPupil = Freeze(new SolidColorBrush(CephalonPalette.PupilDark));
        _brushReflection = Freeze(new SolidColorBrush(WithAlpha(Colors.White, 0.4)));
        _brushDebugBg = Freeze(new SolidColorBrush(WithAlpha(Colors.Black, 0.62)));
        _brushDebugText = Freeze(new SolidColorBrush(WithAlpha(CephalonPalette.TextDim, 0.95)));
        _scleraPen = Freeze(MakePen(WithAlpha(CephalonPalette.ShellEdge, 0.85), 1.2));
        _scleraInnerPen = Freeze(MakePen(WithAlpha(CephalonPalette.ShellEdge, 0.45), 1.0));

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

        // ---- body (everything inside the silhouette tilt) ----
        _tilt.ScaleX = 1.0 + p.ShellTilt;
        _tilt.ScaleY = 1.0 - p.ShellTilt;
        _tilt.CenterX = cx;
        _tilt.CenterY = cy;
        dc.PushTransform(_tilt);

        // 1) inner glow halo — breathing heart of the composition; voice spectrum
        //    (CE-3 axis) adds a live shimmer on top — exactly zero while silent.
        dc.PushOpacity(0.30 + (0.55 * p.GlowIntensity) + (0.10 * p.SpectrumIntensity));
        double glowR = r * (1.55 + 0.35 * p.GlowIntensity) * p.ShellExpansion;
        dc.DrawEllipse(assets.Glow, null, new Point(cx, cy), glowR, glowR);
        dc.Pop();

        // 2) outer segmented ring (slow direct rotation, deformable)
        BuildRing(_outerGeo, cx, cy, r * 1.06 * p.ShellExpansion, segments: 6,
            spanDeg: 40, gapDeg: 20, rotation: p.ShellRotation,
            wobbleAmp: p.WobbleAmp * 0.6, wobblePhase: p.WobblePhase,
            harmonics: animator.WobbleHarmonics, pointsPerArc: PointsPerArc(quality));
        dc.DrawGeometry(null, assets.OuterPen, _outerGeo);

        // 3) middle arcs (counter-rotation, full wobble — the deformable shell)
        BuildRing(_middleGeo, cx, cy, r * 0.90 * p.ShellExpansion, segments: 3,
            spanDeg: 78, gapDeg: 42, rotation: p.Shell2Rotation,
            wobbleAmp: p.WobbleAmp, wobblePhase: (p.WobblePhase * 1.7) + 1.3,
            harmonics: animator.WobbleHarmonics, pointsPerArc: PointsPerArc(quality));
        dc.DrawGeometry(null, assets.MiddlePen, _middleGeo);

        // 4) inner brackets (crisp counterpoint, no wobble)
        BuildRing(_bracketGeo, cx, cy, r * 0.70, segments: 4,
            spanDeg: 22, gapDeg: 68, rotation: p.BracketRotation,
            wobbleAmp: 0.0, wobblePhase: 0.0, harmonics: 1,
            pointsPerArc: Math.Max(6, PointsPerArc(quality) / 2));
        dc.DrawGeometry(null, assets.BracketPen, _bracketGeo);

        // 5) energy spokes between brackets and middle ring
        int spokes = SpokeCount(quality);
        dc.PushOpacity(0.15 + 0.55 * p.GlowIntensity);
        for (int i = 0; i < spokes; i++)
        {
            double a = (p.BracketRotation * 0.5) + (CephalonGeometry.Tau * i / spokes);
            GeoPoint from = CephalonGeometry.Polar(cx, cy, r * 0.74, a);
            GeoPoint to = CephalonGeometry.Polar(cx, cy, r * 0.84, a);
            dc.DrawLine(assets.SpokePen, Pt(from), Pt(to));
        }
        dc.Pop();

        // 6) eye backing disc
        dc.DrawEllipse(_brushShell, _scleraPen, new Point(cx, cy), eyeR, eyeR);
        dc.DrawEllipse(null, _scleraInnerPen, new Point(cx, cy), eyeR * 0.86, eyeR * 0.86);

        // 7) angular iris — TOLIK's own facet signature (octagon, slowly counter-tilting)
        double irisR = eyeR * 0.66;
        double pupilR = eyeR * 0.30 * p.PupilScale;
        int facets = CephalonGeometry.FillFacetPolygon(_buffer, cx, cy, irisR, 8, p.IrisRotation);
        BuildPolygon(_irisGeo, facets);
        dc.DrawGeometry(assets.Iris, null, _irisGeo);
        for (int i = 0; i < facets; i++)
        {
            double a = p.IrisRotation + (CephalonGeometry.Tau * i / facets);
            dc.DrawLine(assets.FacetPen,
                Pt(CephalonGeometry.Polar(cx, cy, pupilR * 0.9, a)),
                Pt(CephalonGeometry.Polar(cx, cy, irisR * 0.94, a)));
        }

        // 8) pupil (dilated pupil travels less — physical, and keeps it inside the iris)
        double travel = eyeR * 0.30 / Math.Max(1.0, p.PupilScale);
        double px = cx + (p.PupilOffsetX * travel);
        double py = cy + (p.PupilOffsetY * travel);
        dc.DrawEllipse(_brushPupil, assets.PupilRimPen, new Point(px, py), pupilR, pupilR);

        // 9) reflection glint
        dc.PushOpacity(0.4);
        dc.DrawEllipse(_brushReflection, null,
            new Point(px - (pupilR * 0.34), py - (pupilR * 0.36)), pupilR * 0.22, pupilR * 0.22);
        dc.Pop();

        // 10) mechanical shutter eyelids
        if (p.EyeOpen < 0.995)
        {
            DrawEyelids(dc, cx, cy, eyeR, p.EyeOpen, assets);
        }

        // 11) energy particles (ambient motes between shell layers)
        DrawParticles(dc, cx, cy, r, time, p.ParticleEnergy, p.SpectrumIntensity, policy, quality, assets);

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

    private void DrawEyelids(DrawingContext dc, double cx, double cy, double eyeR, double open, ToneAssets assets)
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
        dc.DrawGeometry(_brushMembrane, null, _topLidGeo);
        dc.DrawLine(assets.MembraneEdgePen, new Point(x0, topY), new Point(x1, topY));

        using (StreamGeometryContext ctx = _bottomLidGeo.Open())
        {
            ctx.BeginFigure(new Point(x0, cy + eyeR + 6), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(x1, cy + eyeR + 6), true, false);
            ctx.LineTo(new Point(x1, bottomY), true, false);
            ctx.LineTo(new Point(x0, bottomY), true, false);
        }
        dc.DrawGeometry(_brushMembrane, null, _bottomLidGeo);
        dc.DrawLine(assets.MembraneEdgePen, new Point(x0, bottomY), new Point(x1, bottomY));

        dc.Pop(); // clip
    }

    private void DrawParticles(
        DrawingContext dc, double cx, double cy, double r, double time,
        double energy, double spectrum, ModePolicy policy, RenderQuality quality, ToneAssets assets)
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
            double alpha = Math.Clamp(
                (0.25 + (0.75 * energy)) * (0.55 + (0.45 * Math.Sin((time * 2.2) + q.Phase))),
                0.0, 1.0);

            GeoPoint head = CephalonGeometry.Polar(cx, cy, radius, a);
            GeoPoint tail = CephalonGeometry.Polar(cx, cy, radius, a - (0.10 * Math.Sign(q.Speed)));

            dc.PushOpacity(alpha);
            dc.DrawEllipse(assets.Particle, null, Pt(head), q.Size, q.Size);
            dc.DrawEllipse(assets.Particle, null, Pt(tail), q.Size * 0.6, q.Size * 0.6);
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

        return new ToneAssets(
            Iris: Freeze(irisBrush),
            Glow: Freeze(glowBrush),
            Particle: Freeze(new SolidColorBrush(WithAlpha(accent, 0.85))),
            Caption: Freeze(new SolidColorBrush(WithAlpha(accent, 0.80))),
            OuterPen: Freeze(MakePen(WithAlpha(accent, 0.85), 2.2)),
            MiddlePen: Freeze(MakePen(WithAlpha(accent, 0.55), 1.5)),
            BracketPen: Freeze(MakePen(WithAlpha(accent, 0.40), 1.1)),
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
        public Particle(double Angle, double Radius, double Speed, double Size, double Phase)
        {
            this.Angle = Angle;
            this.Radius = Radius;
            this.Speed = Speed;
            this.Size = Size;
            this.Phase = Phase;
        }

        public double Angle { get; }
        public double Radius { get; }
        public double Speed { get; }
        public double Size { get; }
        public double Phase { get; }
    }

    private static Particle[] CreateParticles(int count, ulong seed)
    {
        var rand = new XorShiftRandom(seed);
        var particles = new Particle[count];
        for (int i = 0; i < count; i++)
        {
            particles[i] = new Particle(
                Angle: (CephalonGeometry.Tau * i / count) + rand.Next(-0.2, 0.2),
                Radius: rand.Next(0.84, 1.16),
                Speed: rand.Next(0.12, 0.5) * (i % 2 == 0 ? 1 : -1),
                Size: rand.Next(1.0, 2.4),
                Phase: rand.Next(0.0, CephalonGeometry.Tau));
        }
        return particles;
    }

    private sealed record ToneAssets(
        Brush Iris,
        Brush Glow,
        Brush Particle,
        Brush Caption,
        Pen OuterPen,
        Pen MiddlePen,
        Pen BracketPen,
        Pen SpokePen,
        Pen FacetPen,
        Pen PupilRimPen,
        Pen MembraneEdgePen);
}
