using System.Text.RegularExpressions;

namespace TOLIK.Core;

/// <summary>
/// Voice text cleaning (voice-runtime-v2.md 0.12): markdown never reaches the TTS.
/// Models decorate answers with **bold**, `code`, fences, links, headers and lists —
/// reading decorations aloud is noise. Deterministic, zero model involvement: the
/// voice speaks clean prose only. Fenced code/JSON blocks are dropped from SPEECH
/// entirely (code is for reading, not hearing) — the text clients still receive them.
/// </summary>
public static partial class VoiceTextCleaner
{
    [GeneratedRegex(@"```.*?(?:```|$)", RegexOptions.Singleline)]
    private static partial Regex FencedBlockRegex();

    [GeneratedRegex(@"\[([^\]\n]+)\]\(([^)\n]+)\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"\*\*([^*\n]+)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"(?<!\*)\*([^*\n]+)\*(?!\*)")]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"(?<!\w)__([^_\n]+)__(?!\w)")]
    private static partial Regex BoldUnderscoreRegex();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s*", RegexOptions.Multiline)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^\s*[-*+]\s+", RegexOptions.Multiline)]
    private static partial Regex BulletListRegex();

    [GeneratedRegex(@"^\s*\d+[.)]\s+", RegexOptions.Multiline)]
    private static partial Regex NumberedListRegex();

    public static string CleanForSpeech(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var result = text.Replace("\r\n", "\n");
        result = FencedBlockRegex().Replace(result, " ");     // code/JSON blocks: not spoken
        result = LinkRegex().Replace(result, "$1");           // [text](url) -> text
        result = BoldRegex().Replace(result, "$1");           // **bold** -> bold
        result = BoldUnderscoreRegex().Replace(result, "$1");  // __bold__ -> bold
        result = ItalicRegex().Replace(result, "$1");         // *italic* -> italic
        result = InlineCodeRegex().Replace(result, "$1");     // `code` -> code
        result = HeaderRegex().Replace(result, string.Empty); // ## Header -> Header
        result = BulletListRegex().Replace(result, string.Empty);   // - item -> item
        result = NumberedListRegex().Replace(result, string.Empty); // 1. item -> item
        return result.Trim();
    }
}

/// <summary>
/// Voice response classes (voice-runtime-v2.md 0.11): how much of a reply is SPOKEN.
/// Deterministic on the text — the model never chooses its own budget (§23: model
/// output stays data). The class caps the spoken sentences; the full text always
/// reaches the client through the deltas.
/// </summary>
public enum VoiceResponseClass
{
    /// <summary>Tiny acknowledgment ("Готово") — spoken in full, one phrase.</summary>
    Ack,
    /// <summary>Brief answer — spoken in full (up to 2 sentences).</summary>
    Short,
    /// <summary>Conversational default — first 3 sentences spoken.</summary>
    Normal,
    /// <summary>Long/technical/code — ONE summary sentence spoken, the rest is text.</summary>
    Detailed,
}

public static class VoiceResponsePolicy
{
    /// <summary>Spoken-sentence budgets per class. The text is never truncated — only speech is capped.</summary>
    public static int MaxSpokenSentences(VoiceResponseClass @class) => @class switch
    {
        VoiceResponseClass.Ack => 1,
        VoiceResponseClass.Short => 2,
        VoiceResponseClass.Normal => 3,
        VoiceResponseClass.Detailed => 1,
        _ => 3
    };

    /// <summary>
    /// Deterministic classification: a code fence anywhere → Detailed (code is for
    /// reading); length tiers otherwise. Evolves with the accumulated text during
    /// streaming — the budget only ever tightens mid-stream, never loosens.
    /// </summary>
    public static VoiceResponseClass Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return VoiceResponseClass.Ack;
        if (text.Contains("```", StringComparison.Ordinal)) return VoiceResponseClass.Detailed;
        var length = text.Trim().Length;
        if (length <= 25) return VoiceResponseClass.Ack;
        if (length <= 120) return VoiceResponseClass.Short;
        if (length <= 400) return VoiceResponseClass.Normal;
        return VoiceResponseClass.Detailed;
    }
}
