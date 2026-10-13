using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using TOLIK.Desktop.Bridge;
using TOLIK.Desktop.Rendering;

// WinForms (the tray) shares this assembly's references — pin the WPF types, with
// distinct alias names: inside a Border subclass the HorizontalAlignment/Vertical
// ALIGNMENT property names shadow the enum types, so self-assignments need aliases
// that resolve to the types, not to the inherited properties.
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Rectangle = System.Windows.Shapes.Rectangle;
using WpfHAlign = System.Windows.HorizontalAlignment;
using WpfVAlign = System.Windows.VerticalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace TOLIK.Desktop.Speech;

/// <summary>
/// The mind's voice on the cephalon: a quiet bottom band that SPEAKS the proactive
/// and presence-return messages (both kinds share this one surface — it is the same
/// mind talking; the kind earns only a small cue, never a different voice).
///
/// Design contract (docs/cephalon-visual.md — Ordis-spirit, restrained, precise):
///   • the band composes ON TOP of the 3D cephalon as a sibling element; the
///     renderer's output is never touched and the band is never interactive
///     (IsHitTestVisible=false — in interactive mode clicks still drag the body);
///   • the app's whole text voice is Consolas (caption + debug overlay) — the band
///     speaks the same typeface; colors come from CephalonPalette only, no new hues;
///   • the kind cue is minimal: a hairline accent bar + a 7px diamond — FILLED for
///     proactive (attention, EdgeBlue) and HOLLOW for presence_return (a digest,
///     TextDim — quieter by hierarchy, red/green stay reserved for real states);
///   • the wording is Core's and is presented VERBATIM — this class never rewrites,
///     never truncates (the parser rejects oversized summaries whole upstream);
///   • one well-timed animation per message: fade-in + a small rise (350 ms), a
///     still hold (9 s), fade-out + a small sink (1.2 s) — ~10.6 s visible total;
///     a new line NEVER hard-swaps over a visible one (the SpeechPresenter queues
///     and this class reports <see cref="CycleCompleted"/> to advance it).
/// </summary>
public sealed class CephalonSpeechBand : Border
{
    private const double FadeInMs = 350;
    private const double HoldMs = 9000;
    private const double FadeOutMs = 1200;
    private const double EnterOffset = 10.0; // dips the band rises while fading in
    private const double ExitOffset = 4.0;   // dips it sinks while fading out

    private static readonly FontFamily VoiceFont = new("Consolas");

    private static readonly Geometry Diamond = CreateDiamond();

    private readonly TranslateTransform _enter = new();
    private readonly Rectangle _accent;
    private readonly Path _glyph;
    private readonly TextBlock _summary;
    private Storyboard? _cycle;

    /// <summary>Raised when a full cycle finished (fade-out done) — the App feeds
    /// this to the SpeechPresenter so the next queued line starts its own cycle.</summary>
    public event Action? CycleCompleted;

    public CephalonSpeechBand()
    {
        IsHitTestVisible = false;
        Focusable = false;

        Opacity = 0.0;
        RenderTransform = _enter;
        HorizontalAlignment = WpfHAlign.Center;
        VerticalAlignment = WpfVAlign.Bottom;
        MaxWidth = 340;
        Margin = new Thickness(20, 0, 20, 42); // hovers above the "TOLIK · mode" caption line

        CornerRadius = new CornerRadius(2);
        Background = Solid(CephalonPalette.ShellBase, 0.85);
        BorderBrush = Solid(CephalonPalette.EdgeDim, 0.55);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(12, 9, 14, 10);

        _accent = new Rectangle
        {
            Width = 2,
            RadiusX = 1,
            RadiusY = 1,
            VerticalAlignment = WpfVAlign.Stretch,
            Margin = new Thickness(0, 0, 10, 0),
        };

        _glyph = new Path
        {
            Data = Diamond,
            Width = 7,
            Height = 7,
            Stretch = Stretch.None,
            VerticalAlignment = WpfVAlign.Center,
            Margin = new Thickness(0, 0, 6, 1),
        };

        var wordmark = new TextBlock
        {
            Text = "TOLIK",
            FontFamily = VoiceFont,
            FontSize = 9,
            Foreground = Solid(CephalonPalette.TextDim, 0.90),
            VerticalAlignment = WpfVAlign.Center,
        };

        var labelRow = new StackPanel { Orientation = WpfOrientation.Horizontal };
        labelRow.Children.Add(_glyph);
        labelRow.Children.Add(wordmark);
        labelRow.Margin = new Thickness(0, 0, 0, 5);

        _summary = new TextBlock
        {
            FontFamily = VoiceFont,
            FontSize = 12.5,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Solid(CephalonPalette.IrisBright, 0.92),
        };

        var content = new StackPanel();
        content.Children.Add(labelRow);
        content.Children.Add(_summary);

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(_accent);
        Grid.SetColumn(_accent, 0);
        layout.Children.Add(content);
        Grid.SetColumn(content, 1);

        Child = layout;
    }

    /// <summary>The kind currently on the band (null while idle) — diagnostics/tests.</summary>
    public MessageKind? ShowingKind { get; private set; }

    /// <summary>The verbatim line currently on the band — diagnostics/tests.</summary>
    public string? SummaryText => _summary.Text;

    /// <summary>Begins a full speak-cycle for the message. The presenter guarantees
    /// this is only called while the band is idle — never over a visible line.</summary>
    public void Show(ProactiveMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        Apply(message);

        _cycle?.Stop(this);
        Storyboard cycle = BuildCycle();
        cycle.Completed += (_, _) => CycleCompleted?.Invoke();
        _cycle = cycle;
        cycle.Begin(this);
    }

    /// <summary>Applies the message to the band's surface — verbatim text plus the
    /// kind cue (accent bar + diamond). No animation, no dispatcher work: testable
    /// headless on an STA thread (internals are visible to TOLIK.Desktop.Tests).</summary>
    internal void Apply(ProactiveMessage message)
    {
        ShowingKind = message.Kind;
        _summary.Text = message.Summary;

        bool attention = message.Kind == MessageKind.Proactive;
        Color accent = attention ? CephalonPalette.EdgeBlue : CephalonPalette.TextDim;
        _accent.Fill = Solid(accent, attention ? 0.95 : 0.80);
        if (attention)
        {
            _glyph.Fill = Solid(accent, 0.95);
            _glyph.Stroke = null;
        }
        else
        {
            _glyph.Fill = null;
            _glyph.Stroke = Solid(accent, 0.90);
            _glyph.StrokeThickness = 1;
        }
    }

    /// <summary>One orchestrated cycle: rise+fade-in → still hold → sink+fade-out.
    /// BeginTime offsets create the hold; FillBehavior (default HoldEnd) keeps each
    /// phase's final value until the next phase — no flicker, no timers of our own.</summary>
    private Storyboard BuildCycle()
    {
        var fadeIn = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(FadeInMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var rise = new DoubleAnimation
        {
            From = EnterOffset,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(FadeInMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var fadeOut = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            BeginTime = TimeSpan.FromMilliseconds(FadeInMs + HoldMs),
            Duration = TimeSpan.FromMilliseconds(FadeOutMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        var sink = new DoubleAnimation
        {
            From = 0.0,
            To = ExitOffset,
            BeginTime = TimeSpan.FromMilliseconds(FadeInMs + HoldMs),
            Duration = TimeSpan.FromMilliseconds(FadeOutMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };

        var cycle = new Storyboard();
        Add(cycle, fadeIn, this, "Opacity");
        Add(cycle, rise, _enter, "Y");
        Add(cycle, fadeOut, this, "Opacity");
        Add(cycle, sink, _enter, "Y");
        return cycle;
    }

    private static void Add(Storyboard cycle, DoubleAnimation animation, DependencyObject target, string property)
    {
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        cycle.Children.Add(animation);
    }

    private static Geometry CreateDiamond()
    {
        Geometry diamond = Geometry.Parse("M3.5,0 L7,3.5 L3.5,7 L0,3.5 Z");
        diamond.Freeze();
        return diamond;
    }

    private static SolidColorBrush Solid(Color color, double alpha)
    {
        byte a = (byte)Math.Clamp((int)Math.Round(alpha * 255.0), 0, 255);
        var brush = new SolidColorBrush(Color.FromArgb(a, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
