using System.Windows.Media;
using TOLIK.Desktop.Motion;

// WinForms/WPF shared framework refs both define Color — pin the WPF one for this file.
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace TOLIK.Desktop.Rendering;

/// <summary>
/// Color system. Primary: blue family. Red is defined but ONLY mapped from
/// Error/Locked tones (real security/authorization states, Phase 2/3) — the
/// Phase 1 idle palette contains no red, and tone is never chosen by mood guesswork.
/// </summary>
public static class CephalonPalette
{
    public static readonly Color ShellBase = FromHex("#0C1322");   // eye backing / membranes
    public static readonly Color ShellEdge = FromHex("#1B2C46");   // dim structural edges
    public static readonly Color EdgeBlue = FromHex("#58A6FF");    // primary accent (reference vocabulary)
    public static readonly Color EdgeDim = FromHex("#34507A");     // secondary structural blue
    public static readonly Color IrisBright = FromHex("#A8D4FF");  // iris core
    public static readonly Color IrisDeep = FromHex("#1C3D6E");    // iris rim
    public static readonly Color PupilDark = FromHex("#04060C");
    public static readonly Color VoiceGreen = FromHex("#3FB950");  // reserved: speaking tone
    public static readonly Color AlarmRed = FromHex("#F85149");    // reserved: denied/locked tones ONLY
    public static readonly Color TextDim = FromHex("#8FA6C4");

    /// <summary>(iris fill, accent line) per tone. Unmapped tones fail closed to Calm.</summary>
    public static (Color Iris, Color Accent) Resolve(CephalonTone tone) => tone switch
    {
        CephalonTone.Working => (EdgeBlue, EdgeBlue),
        CephalonTone.Voice => (VoiceGreen, VoiceGreen),
        CephalonTone.Error => (AlarmRed, AlarmRed),   // Phase 2/3 only — real events required
        CephalonTone.Locked => (AlarmRed, AlarmRed),  // Phase 2/3 only — real events required
        _ => (EdgeBlue, EdgeBlue),                    // Calm and anything unknown → calm blue
    };

    private static Color FromHex(string hex) =>
        (Color)ColorConverter.ConvertFromString(hex);
}
