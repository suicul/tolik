namespace TOLIK.Desktop.Performance;

/// <summary>
/// Adaptive render quality level. The governor lowers/raises it based on measured
/// frame time; the composition maps the level to particle counts / arc resolution.
/// </summary>
public enum RenderQuality
{
    Minimal = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>
/// Frame governor (AGENTS §16 spirit): caps FPS to the mode policy, measures render
/// frame time (EWMA) and adapts quality when over budget. Pure logic — no WPF, no
/// timers, fully deterministic under tests.
/// </summary>
public sealed class FrameGovernor
{
    public const double EwmaAlpha = 0.12;
    public const double DegradeFactor = 1.30;   // degrade when ewma > budget * 1.30
    public const double RecoverFactor = 0.55;   // candidate recovery when ewma <= budget * 0.55
    public const double MinChangeCooldownSec = 1.5;
    public const double RecoverHoldSec = 2.0;

    public const double MinFps = 1.0;
    public const double MaxFps = 120.0;

    private double _targetFps;
    private double _lastRenderAt;
    private bool _hasLastRender;
    private double _ewmaMs;
    private bool _hasEwma;
    private RenderQuality _quality = RenderQuality.High;
    private double _lastQualityChangeAt = double.NegativeInfinity;
    private double? _goodSince;

    public FrameGovernor(double targetFps)
    {
        _targetFps = SanitizeFps(targetFps);
    }

    /// <summary>Current FPS ceiling (always within [MinFps, MaxFps]).</summary>
    public double TargetFps => _targetFps;

    /// <summary>CPU render budget in milliseconds: half of the frame interval.
    /// The rest of the frame belongs to the OS/game — TOLIK must stay light.</summary>
    public double BudgetMs => 500.0 / _targetFps;

    public RenderQuality Quality => _quality;

    /// <summary>EWMA of measured render frame time in milliseconds (0 until first frame).</summary>
    public double EwmaFrameMs => _ewmaMs;

    /// <summary>Changes the FPS ceiling (clamped). Used on mode switches.</summary>
    public void SetTargetFps(double fps)
    {
        _targetFps = SanitizeFps(fps);
    }

    /// <summary>Decides whether this WPF frame should actually render. Cheap gate —
    /// called on every CompositionTarget.Rendering tick, allocates nothing.</summary>
    public bool ShouldRender(double nowSeconds)
    {
        double minInterval = 1.0 / _targetFps;
        // 0.92 tolerance absorbs vsync jitter so we don't systematically drop to (n-1) fps.
        if (!_hasLastRender || nowSeconds - _lastRenderAt >= minInterval * 0.92)
        {
            _lastRenderAt = nowSeconds;
            _hasLastRender = true;
            return true;
        }
        return false;
    }

    /// <summary>Feeds back the measured render time (ms) of one rendered frame and adapts quality.</summary>
    public void OnFrameRendered(double frameMs, double nowSeconds)
    {
        double sample = double.IsFinite(frameMs) ? Math.Clamp(frameMs, 0.0, 10_000.0) : 0.0;
        _ewmaMs = _hasEwma ? _ewmaMs + EwmaAlpha * (sample - _ewmaMs) : sample;
        _hasEwma = true;

        if (nowSeconds - _lastQualityChangeAt < MinChangeCooldownSec)
        {
            return;
        }

        double budget = BudgetMs;

        // Degrade fast, recover slow (hysteresis prevents flapping).
        if (_quality > RenderQuality.Minimal && _ewmaMs > budget * DegradeFactor)
        {
            _quality--;
            _lastQualityChangeAt = nowSeconds;
            _goodSince = null;
        }
        else if (_quality < RenderQuality.High && _ewmaMs <= budget * RecoverFactor)
        {
            _goodSince ??= nowSeconds;
            if (nowSeconds - _goodSince.Value >= RecoverHoldSec)
            {
                _quality++;
                _lastQualityChangeAt = nowSeconds;
                _goodSince = null;
            }
        }
        else
        {
            _goodSince = null;
        }
    }

    private static double SanitizeFps(double fps) =>
        double.IsFinite(fps) ? Math.Clamp(fps, MinFps, MaxFps) : MinFps;
}
