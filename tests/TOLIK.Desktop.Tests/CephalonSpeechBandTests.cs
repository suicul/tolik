using System.Windows;
using System.Windows.Media;
using TOLIK.Desktop.Bridge;
using TOLIK.Desktop.Rendering;
using TOLIK.Desktop.Speech;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// The mind's voice band, verified on an STA thread (no dispatcher pump, no
/// storyboard run — only the surface): the band builds honest chrome, applies BOTH
/// message kinds with the wording VERBATIM, and keeps the kind cues inside the
/// app's palette (attention blue for proactive, quiet dim for the digest — red and
/// green stay reserved for real states).
/// </summary>
public class CephalonSpeechBandTests
{
    [Fact]
    public void Band_builds_quiet_and_non_interactive()
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                var band = new CephalonSpeechBand();

                Assert.Equal(0.0, band.Opacity);          // silent until spoken to
                Assert.False(band.IsHitTestVisible);      // never fights the user
                Assert.Null(band.ShowingKind);
                Assert.Equal(string.Empty, band.SummaryText);
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
    }

    [Fact]
    public void Proactive_applies_verbatim_text_and_attention_cue()
    {
        Exception? captured = null;
        ProactiveMessage? applied = null;
        var thread = new Thread(() =>
        {
            try
            {
                var band = new CephalonSpeechBand();
                applied = new ProactiveMessage(
                    MessageKind.Proactive,
                    "Замечена серия неудач: 3 задач подряд не завершились. Стоит разобраться.",
                    At: null);

                band.Apply(applied);

                Assert.Equal(MessageKind.Proactive, band.ShowingKind);
                Assert.Equal(applied.Summary, band.SummaryText); // verbatim — never rewritten
                Assert.Equal(SolidColor(CephalonPalette.EdgeBlue, 0.95), ExtractFill(band));
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
    }

    [Fact]
    public void Presence_return_applies_verbatim_text_and_digest_cue()
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                var band = new CephalonSpeechBand();

                band.Apply(new ProactiveMessage(
                    MessageKind.PresenceReturn,
                    "За последний час: 3 задачи завершено, 1 провалена, 2 взаимодействия.",
                    At: null));

                Assert.Equal(MessageKind.PresenceReturn, band.ShowingKind);
                Assert.Equal("За последний час: 3 задачи завершено, 1 провалена, 2 взаимодействия.", band.SummaryText);
                Assert.Equal(SolidColor(CephalonPalette.TextDim, 0.80), ExtractFill(band));
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
    }

    private static Color SolidColor(Color baseColor, double alpha)
    {
        byte a = (byte)Math.Clamp((int)Math.Round(alpha * 255.0), 0, 255);
        return Color.FromArgb(a, baseColor.R, baseColor.G, baseColor.B);
    }

    private static Color ExtractFill(CephalonSpeechBand band)
    {
        // The accent bar is the band's first layout child: its fill IS the kind cue.
        var grid = (System.Windows.Controls.Grid)band.Child;
        var accent = (System.Windows.Shapes.Rectangle)grid.Children[0];
        var brush = (SolidColorBrush)accent.Fill!;
        return brush.Color;
    }
}
