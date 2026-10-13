using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TOLIK.Desktop.Motion;
using TOLIK.Desktop.Performance;
using TOLIK.Desktop.Presence;
using TOLIK.Desktop.Render3D;

// The WindowsDesktop SDK (UseWindowsForms) adds implicit System.Drawing /
// System.Windows.Forms usings that clash with WPF types — pin WPF for this file.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using Point = System.Windows.Point;

namespace TOLIK.Desktop.Rendering;

/// <summary>
/// Draws one frame of the Cephalon from <see cref="CephalonParameters"/> + policy +
/// quality. The composition is a pure function of state: no identity, no memory,
/// no permissions, no network, no clock of its own — it renders what it is told.
///
/// The body is REAL 3D now (owner verdict: «цефалон должен быть более объемным,
/// может быть попробовать 3д?»): <see cref="Cephalon3DPipeline"/> — a small software
/// renderer (no Viewport3D, no GPU requirement, AGENTS §6) — transforms the
/// parametric mesh, projects it through a real perspective camera, culls
/// backfaces, flat-shades every triangle under the fixed upper-left key light
/// with the emissive grade, and hands back a painter-ordered draw list. This
/// class only maps that list to cached frozen WPF brushes. The mesh itself
/// stays parametric and transferable — the design source for the VR/Android
/// cephalon (docs/VR.md).
///
/// Composition: the shell is a stack of tilted arc bands at real z-depths
/// (far hull band, mid chassis arcs, and the outer anchor arcs that replaced
/// the corner brackets — «ограничивающие рамки, их прям видно» — with 2–3 arcs
/// of the OUTERMOST ring, a natural cage, never a UI frame); the eye is a real
/// lat-long sphere with the faceted octagonal gem iris, the gaze-offset pupil
/// cap and hemispherical shutter lids that occlude mechanically; the reactor
/// glow stays a 2D backdrop behind the body; motes orbit in 3D between the
/// shell depths. Volume is structural: a default body tilt, per-ring plane
/// tilts and the perspective camera do the work the 2D depth pass faked.
///
/// Allocation discipline (AGENTS §43/§16): the pipeline reuses its vertex,
/// face and group buffers; brushes are cached per (tone, material, shade
/// bucket) and frozen; each draw group fills one pooled StreamGeometry that is
/// reopened every frame (the Phase 1 pattern); the only per-frame text
/// allocation is the debug overlay, rebuilt at most 4×/s.
/// </summary>
public sealed class CephalonComposition
{
    // --- 3D core (framework-free) + WPF-facing resources ---
    private readonly Cephalon3DPipeline _pipeline = new();

    private readonly Brush _brushGlint;
    private readonly Brush _brushDebugBg;
    private readonly Brush _brushDebugText;
    private readonly Typeface _typeface = new("Consolas");

    /// <summary>Pooled draw geometries — one per emitted group per frame, reused.</summary>
    private readonly List<StreamGeometry> _geoPool = new(160);
    private int _geoUsed;

    /// <summary>(tone, material, bucket) → frozen flat-shade brush.</summary>
    private readonly Dictionary<int, Brush> _faceBrushes = new();

    // --- per-tone assets (glow stack, motes, caption follow the state tone) ---
    private readonly Dictionary<CephalonTone, ToneAssets> _toneAssets;

    // --- cached text ---
    private FormattedText? _caption;
    private string _captionKey = string.Empty;
    private FormattedText? _debugText;
    private double _lastDebugRefreshAt = double.NegativeInfinity;

    public CephalonComposition()
    {
        // Specular glint — a hot core with a soft falloff (glass, not paint).
        var glintStops = new GradientStopCollection
        {
            new GradientStop(WithAlpha(Colors.White, 0.95), 0.0),
            new GradientStop(WithAlpha(Colors.White, 0.30), 0.55),
            new GradientStop(WithAlpha(Colors.White, 0.0), 1.0),
        };
        _brushGlint = Freeze(new RadialGradientBrush(glintStops));
        _brushDebugBg = Freeze(new SolidColorBrush(WithAlpha(Colors.Black, 0.62)));
        _brushDebugText = Freeze(new SolidColorBrush(WithAlpha(CephalonPalette.TextDim, 0.95)));

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
        ToneAssets assets = GetAssets(animator.Traits.Tone);
        DepthShedding shed = CephalonDepth.Shed(quality);

        // 1) reactor glow stack (the 2D backdrop — the light source behind the
        //    3D body): a wide atmospheric halo, the breathing main glow, and a
        //    hot core that the 3D eye and bands occlude, so the light reads as
        //    coming from INSIDE the machine. Voice spectrum (CE-3 axis)
        //    shimmers the whole stack equally — exactly zero while silent.
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

        // 2) the 3D body — the whole layered shell, the eye, the gem iris, the
        //    pupil, the shutter lids and the motes, painter-sorted far→near.
        int moteLimit = policy.AllowParticles ? ParticleMax(quality) : 0;
        Scene3DFrame scene = _pipeline.Render(
            p, animator.WobbleHarmonics, animator.TimeSeconds, quality, cx, cy, size, moteLimit);
        DrawScene(dc, scene, animator.Traits.Tone, policy, shed);

        // ---- captions & diagnostics ----
        if (policy.ShowCaption)
        {
            DrawCaption(dc, width, height, r, pixelsPerDip, assets);
        }

        if (showDebug && policy.AllowDebugOverlay)
        {
            DrawDebugOverlay(dc, pixelsPerDip, policy, quality, debugHotkeyLine, measuredFps, nowSeconds);
        }
    }

    // ---------- 3D scene drawing ----------

    private void DrawScene(
        DrawingContext dc, Scene3DFrame scene, CephalonTone tone, ModePolicy policy, DepthShedding shed)
    {
        if (scene.GroupCount == 0 && scene.MoteCount == 0)
        {
            return;
        }

        _geoUsed = 0;
        CephalonTone activeTone = _toneAssets.ContainsKey(tone) ? tone : CephalonTone.Calm;

        if (policy.AllowParticles && scene.MoteCount > 0 && scene.GroupCount == 0)
        {
            // no faces at all (defensive): the motes are the whole frame
            DrawMotes(dc, scene, scene.MoteCount, GetAssets(activeTone));
        }

        int moteIdx = 0;
        for (int gi = 0; gi < scene.GroupCount; gi++)
        {
            SceneGroup g = scene.Groups[gi];

            // Motes of this slice (and any slice behind the first group) draw
            // first — the painter's order keeps the depth read honest.
            while (moteIdx < scene.MoteCount && scene.Motes[moteIdx].Slice <= g.Slice)
            {
                if (policy.AllowParticles)
                {
                    DrawMote(dc, scene.Motes[moteIdx], GetAssets(activeTone));
                }
                moteIdx++;
            }

            StreamGeometry geo = NextGeometry();
            using (StreamGeometryContext ctx = geo.Open())
            {
                int end = g.Start + g.Count;
                for (int i = g.Start; i < end; i++)
                {
                    SceneFace f = scene.Faces[i];
                    ctx.BeginFigure(new Point(f.X0, f.Y0), isFilled: true, isClosed: true);
                    ctx.LineTo(new Point(f.X1, f.Y1), isStroked: true, isSmoothJoin: false);
                    ctx.LineTo(new Point(f.X2, f.Y2), isStroked: true, isSmoothJoin: false);
                }
            }
            dc.DrawGeometry(FaceBrush(activeTone, g), null, geo);
        }

        // front-most motes (slices beyond the last face group)
        if (policy.AllowParticles && moteIdx < scene.MoteCount)
        {
            DrawMotes(dc, scene, scene.MoteCount - moteIdx, GetAssets(activeTone), from: moteIdx);
        }

        // 3) speculars — a hot key-light glint up-left of the pupil, plus a
        //    faint bounce light low-right (DirectionalShading tier): the eye
        //    reads as wet glass under one consistent light. Gated by the
        //    shutter: a closed eye has no wet surface to reflect.
        if (scene.GlintVisible)
        {
            double d = scene.GlintRadius;
            dc.PushOpacity(0.4);
            dc.DrawEllipse(_brushGlint, null,
                new Point(scene.GlintX - (d * 0.34), scene.GlintY - (d * 0.36)), d * 0.22, d * 0.22);
            dc.Pop();
            if (shed.DirectionalShading)
            {
                dc.PushOpacity(0.28);
                dc.DrawEllipse(GetAssets(activeTone).Particle, null,
                    new Point(scene.GlintX + (d * 0.38), scene.GlintY + (d * 0.34)), d * 0.12, d * 0.12);
                dc.Pop();
            }
        }
    }

    private void DrawMote(DrawingContext dc, SceneMote mote, ToneAssets assets)
    {
        if (mote.Alpha <= 0.01 || mote.Size <= 0.05)
        {
            return;
        }
        dc.PushOpacity(mote.Alpha);
        dc.DrawEllipse(assets.Particle, null, new Point(mote.X, mote.Y), mote.Size, mote.Size);
        dc.Pop();
    }

    private void DrawMotes(DrawingContext dc, Scene3DFrame scene, int count, ToneAssets assets, int from = 0)
    {
        for (int i = from; i < from + count; i++)
        {
            DrawMote(dc, scene.Motes[i], assets);
        }
    }

    private StreamGeometry NextGeometry()
    {
        if (_geoUsed == _geoPool.Count)
        {
            _geoPool.Add(new StreamGeometry());
        }
        return _geoPool[_geoUsed++];
    }

    // ---------- shading colors (the emissive grade, palette unchanged) ----------

    private Brush FaceBrush(CephalonTone tone, SceneGroup g)
    {
        int key = (((int)tone * (int)MaterialKind.Count) + g.Material) * Cephalon3D.ShadeBuckets + g.Bucket;
        if (_faceBrushes.TryGetValue(key, out Brush? brush))
        {
            return brush;
        }

        (Color dark, Color lit) = MaterialColors(tone, (MaterialKind)g.Material);
        double t = g.Bucket / (double)(Cephalon3D.ShadeBuckets - 1);
        double alpha = Cephalon3D.Material((MaterialKind)g.Material).Alpha;
        Color c = WithAlpha(Lerp(dark, lit, t), alpha);
        brush = Freeze(new SolidColorBrush(c));
        _faceBrushes[key] = brush;
        return brush;
    }

    /// <summary>The dark (emissive floor) and lit (hot) ends of each material's shade ramp.</summary>
    private static (Color Dark, Color Lit) MaterialColors(CephalonTone tone, MaterialKind material)
    {
        (Color iris, Color accent) = CephalonPalette.Resolve(tone);
        return material switch
        {
            MaterialKind.ShellOuter => (Lerp(accent, CephalonPalette.PupilDark, 0.62), Lift(accent, 0.16)),
            MaterialKind.ShellMiddle => (Lerp(accent, CephalonPalette.PupilDark, 0.66), accent),
            MaterialKind.ShellAnchor => (Lerp(accent, CephalonPalette.PupilDark, 0.55), Lift(accent, 0.26)),
            MaterialKind.Sclera => (
                Lerp(CephalonPalette.ShellBase, CephalonPalette.PupilDark, 0.55),
                Lerp(CephalonPalette.ShellBase, CephalonPalette.EdgeDim, 0.60)),
            MaterialKind.Iris => (CephalonPalette.IrisDeep, Lift(CephalonPalette.IrisBright, 0.10)),
            MaterialKind.Pupil => (CephalonPalette.PupilDark, Lerp(CephalonPalette.PupilDark, CephalonPalette.ShellBase, 0.35)),
            MaterialKind.Lid => (
                Lerp(CephalonPalette.ShellBase, CephalonPalette.PupilDark, 0.45),
                Lerp(accent, CephalonPalette.ShellBase, 0.35)),
            _ => (CephalonPalette.ShellBase, CephalonPalette.ShellEdge),
        };
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
        _ = iris; // the iris color now lives in the 3D material ramp

        var glowStops = new GradientStopCollection
        {
            new GradientStop(WithAlpha(accent, 0.40), 0.0),
            new GradientStop(WithAlpha(accent, 0.10), 0.55),
            new GradientStop(WithAlpha(accent, 0.0), 1.0),
        };
        var glowBrush = new RadialGradientBrush(glowStops);

        // Volumetric glow stack: a wide atmospheric halo enveloping the whole
        // body, and a hot reactor core behind the eye — the 3D bands and the
        // globe occlude it, so the light reads as coming from INSIDE.
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

        return new ToneAssets(
            Glow: Freeze(glowBrush),
            GlowWide: Freeze(new RadialGradientBrush(glowWideStops)),
            GlowCore: Freeze(new RadialGradientBrush(glowCoreStops)),
            Particle: Freeze(new SolidColorBrush(WithAlpha(accent, 0.85))),
            Caption: Freeze(new SolidColorBrush(WithAlpha(accent, 0.80))));
    }

    private static Color WithAlpha(Color c, double opacity)
    {
        byte a = (byte)Math.Clamp((int)Math.Round(opacity * 255.0), 0, 255);
        return Color.FromArgb(a, c.R, c.G, c.B);
    }

    /// <summary>Linear blend between two palette colors (t clamped to [0,1]).</summary>
    private static Color Lerp(Color a, Color b, double t)
    {
        double k = Math.Clamp(t, 0.0, 1.0);
        return Color.FromArgb(
            255,
            (byte)Math.Round(a.R + ((b.R - a.R) * k)),
            (byte)Math.Round(a.G + ((b.G - a.G) * k)),
            (byte)Math.Round(a.B + ((b.B - a.B) * k)));
    }

    /// <summary>Lift a color toward white (specular side).</summary>
    private static Color Lift(Color c, double t) => Lerp(c, Colors.White, t);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private sealed record ToneAssets(
        Brush Glow,
        Brush GlowWide,
        Brush GlowCore,
        Brush Particle,
        Brush Caption);
}
