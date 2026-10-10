using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using TOLIK.Desktop.Bridge;
using TOLIK.Desktop.Motion;
using TOLIK.Desktop.Performance;
using TOLIK.Desktop.Presence;

namespace TOLIK.Desktop.Rendering;

/// <summary>
/// WPF host element: owns the DrawingVisual, the render loop
/// (CompositionTarget.Rendering + FrameGovernor) and the animator.
/// This is the only place WPF time meets the pure motion core.
///
/// It is a RENDERER: it contains no identity, no memory, no permissions,
/// no authorization and no network logic (AGENTS §13). Honest diagnostics only.
/// </summary>
public sealed class CephalonHost : FrameworkElement
{
    private readonly DrawingVisual _visual = new();
    private readonly CephalonAnimator _animator;
    private readonly FrameGovernor _governor;
    private readonly CephalonComposition _composition = new();
    private readonly ExpressionLinkPresenter _link;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _measure = new();
    private readonly string? _debugExtraLine;

    private PresenceMode _mode = PresenceMode.Normal;
    private ModePolicy _policy = ModePolicyTable.GetSafe(PresenceMode.Normal);
    private bool _debugOverlay;
    private bool _hooked;
    private double _lastEventTime;
    private bool _hasEventTime;
    private double _pixelsPerDip = 1.0;

    // measured-fps window (for honest diagnostics)
    private double _fpsWindowStart;
    private int _fpsFrames;
    private double _measuredFps;

    public CephalonHost()
        : this(IdleMotionConfig.Default, new XorShiftRandom(seed: 0x5EEDC0DEUL), debugExtraLine: null)
    {
    }

    /// <summary>Injection constructor for tests/experiments: config, random source, hotkey status line.</summary>
    public CephalonHost(IdleMotionConfig config, IRandom random, string? debugExtraLine)
    {
        _animator = new CephalonAnimator(config, random);
        _governor = new FrameGovernor(_policy.TargetFps);
        _link = new ExpressionLinkPresenter(_animator);
        _debugExtraLine = debugExtraLine;
        _composition.ModeName = _mode.ToString();

        AddVisualChild(_visual);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public CephalonAnimator Animator => _animator;

    public FrameGovernor Governor => _governor;

    /// <summary>CE-2 link presentation policy (drive vs honest idle, debug lines).
    /// The App feeds it client snapshots/frames; the render loop ticks staleness.</summary>
    public ExpressionLinkPresenter CoreLink => _link;

    /// <summary>Link state snapshot from the App's WS client (UI thread).</summary>
    public void OnCoreLinkState(LinkSnapshot snapshot) => _link.OnLinkState(snapshot);

    /// <summary>A real expression frame from Core (UI thread) — becomes animator
    /// targets; the body eases there (nothing snaps).</summary>
    public void OnCoreExpressionFrame(ExpressionFrame frame) => _link.OnExpressionFrame(frame);

    /// <summary>Applies a presence mode: FPS ceiling, motion scale, overlay permissions.
    /// The debug flag is only kept if the new mode still allows diagnostics.</summary>
    public void ApplyMode(PresenceMode mode, ModePolicy policy)
    {
        _mode = mode;
        _policy = policy;
        _composition.ModeName = mode.ToString();
        _governor.SetTargetFps(policy.TargetFps);
        _animator.MotionScale = policy.AllowMotion ? policy.IdleIntensity : 0.0;
    }

    public void SetDebugOverlay(bool enabled)
    {
        _debugOverlay = enabled;
    }

    private bool DebugEffective => _debugOverlay && _policy.AllowDebugOverlay;

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _visual;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _pixelsPerDip = newDpi.PixelsPerDip;
        base.OnDpiChanged(oldDpi, newDpi);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked)
        {
            CompositionTarget.Rendering += OnRendering;
            _hooked = true;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = _hasEventTime ? now - _lastEventTime : 1.0 / 60.0;
        _lastEventTime = now;
        _hasEventTime = true;

        // CE-2: age the Core link (drops a stale drive — never a held pose on a
        // dead stream) and refresh the honest debug lines. Cheap; every tick.
        _link.Tick();
        _composition.CoreLine = _link.CoreLine;
        _composition.ExpressionLine = _link.ExpressionLine;
        _composition.ReasonLine = _link.ReasonLine;

        if (!_governor.ShouldRender(now))
        {
            return;
        }

        // Advance the body with real elapsed time (gated frames keep correct timing).
        _animator.Update(dt);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 1.0 || h <= 1.0)
        {
            return;
        }

        _composition.GovernorFrameMs = _governor.EwmaFrameMs;
        _measure.Restart();
        using (DrawingContext dc = _visual.RenderOpen())
        {
            _composition.Render(
                dc, w, h, _animator, _policy, _governor.Quality, _pixelsPerDip,
                DebugEffective, _debugExtraLine, _measuredFps, now);
        }
        _measure.Stop();
        _governor.OnFrameRendered(_measure.Elapsed.TotalMilliseconds, now);

        // measured fps window
        _fpsFrames++;
        if (now - _fpsWindowStart >= 1.0)
        {
            _measuredFps = _fpsFrames / (now - _fpsWindowStart);
            _fpsWindowStart = now;
            _fpsFrames = 0;
        }
    }
}
