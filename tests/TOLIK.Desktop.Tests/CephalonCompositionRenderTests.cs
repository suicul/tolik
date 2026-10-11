using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TOLIK.Desktop.Motion;
using TOLIK.Desktop.Performance;
using TOLIK.Desktop.Presence;
using TOLIK.Desktop.Rendering;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// End-to-end smoke of the depth pass (owner tuning pass "add volume"): the real
/// WPF composition must render without exceptions at EVERY quality tier and with
/// the shutter engaged (gradient membranes), produce actual pixels, and the
/// shedding ladder must produce visibly different output between High and
/// Minimal — proving the depth effects participate in the adaptive quality
/// ladder instead of being silently skipped. Deterministic: fixed seed, fixed
/// advance, pure composition function of parameters.
/// </summary>
public class CephalonCompositionRenderTests
{
    [Fact]
    public void Depth_pass_renders_at_every_tier_and_the_ladder_changes_the_frame()
    {
        // WPF drawing requires an STA thread; xunit hosts are MTP.
        Exception? captured = null;
        var report = new Dictionary<string, (double LitFraction, bool LadderDiffers, bool LidsRender)>();

        var thread = new Thread(() =>
        {
            try
            {
                ModePolicy policy = ModePolicyTable.GetSafe(PresenceMode.Normal);

                foreach (RenderQuality quality in Enum.GetValues<RenderQuality>())
                {
                    var animator = MakeAdvancedAnimator();
                    byte[] pixels = RenderToPixels(animator, policy, quality);
                    double lit = LitFraction(pixels);

                    bool ladderDiffers = false;
                    if (quality == RenderQuality.High)
                    {
                        // Same state, flat fallback: the two frames must differ —
                        // the depth stack is really drawn, really shed.
                        byte[] minimal = RenderToPixels(MakeAdvancedAnimator(), policy, RenderQuality.Minimal);
                        ladderDiffers = Differs(pixels, minimal);
                    }

                    // Shutter half-closed exercises the gradient membrane path.
                    var blinking = MakeAdvancedAnimator();
                    blinking.Parameters.EyeOpen = 0.3;
                    byte[] lidPixels = RenderToPixels(blinking, policy, quality);
                    bool lidsRender = LitFraction(lidPixels) > 0.2;

                    report[quality.ToString()] = (lit, ladderDiffers, lidsRender);
                }
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(captured);
        Assert.Equal(4, report.Count); // Minimal, Low, Medium, High all rendered

        foreach (RenderQuality quality in Enum.GetValues<RenderQuality>())
        {
            (double lit, bool ladderDiffers, bool lidsRender) = report[quality.ToString()];
            Assert.InRange(lit, 0.2, 1.0); // real pixels at every tier — presence is never blank
            Assert.True(lidsRender, $"{quality}: shutter membranes must render");
        }

        Assert.True(report[nameof(RenderQuality.High)].LadderDiffers,
            "High tier must draw the depth stack differently from the flat Minimal fallback");
    }

    private static CephalonAnimator MakeAdvancedAnimator()
    {
        var animator = new CephalonAnimator(IdleMotionConfig.Default, new XorShiftRandom(0x5EEDC0DEUL));
        for (double t = 0; t < 3.0; t += 1.0 / 60.0)
        {
            animator.Update(1.0 / 60.0);
        }
        return animator;
    }

    private static byte[] RenderToPixels(CephalonAnimator animator, ModePolicy policy, RenderQuality quality)
    {
        const double size = 380.0;
        var composition = new CephalonComposition();
        composition.ModeName = "Normal";

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            composition.Render(
                dc, size, size, animator, policy, quality,
                pixelsPerDip: 1.0, showDebug: false, debugHotkeyLine: null,
                measuredFps: 30.0, nowSeconds: 0.0);
        }

        var bitmap = new RenderTargetBitmap((int)size, (int)size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        int stride = (int)size * 4;
        var pixels = new byte[stride * (int)size];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static double LitFraction(byte[] pixels)
    {
        int lit = 0;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 8)
            {
                lit++;
            }
        }
        return lit / (double)(pixels.Length / 4);
    }

    private static bool Differs(byte[] a, byte[] b)
    {
        int diff = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            int d = Math.Abs(a[i] - b[i])
                + Math.Abs(a[i + 1] - b[i + 1])
                + Math.Abs(a[i + 2] - b[i + 2]);
            if (d > 6)
            {
                diff++;
            }
        }
        return diff > (a.Length / 4) / 100; // at least 1% of pixels visibly different
    }
}
