namespace TOLIK.Core;

/// <summary>
/// Fixed enrollment prompts for the Genesis Owner Voice Enrollment (docs/voice-identity.md
/// §4, VI-3): five phonetically DISTINCT Russian pangram-style sentences covering
/// шипящие (ш/ж/ч/щ), свистящие (с/з/ц), глухие (п/ф/х/т/к), звонкие (б/в/г/д/з) and
/// the р/л contrast. The texts are fixed public prompts — NOT secrets (the client
/// records the spoken phrase and posts only the embedding; raw audio never persists).
/// </summary>
public static class GenesisVoicePhrases
{
    public static IReadOnlyList<string> Phrases { get; } =
    [
        // шипящие (ш/ж/щ/ч) + свистящие (с/ц) + звонкие (б/в/г/д/з) + р-л: широкая/губерний/сельского
        "Широкая электрификация южных губерний даст мощный толчок подъёму сельского хозяйства.",
        // свистящие (с/ц) + глухие (ш/ф/х/т/к) + звонкие (ж) + р-л: французских
        "Съешь же ещё этих мягких французских булок, да выпей чаю.",
        // аффриката (ц) + свистящие (с/з) + шипящие (ш) + глухие (ф/т/х) + р-л: ландшафта/героев
        "Аэрофотосъёмка ландшафта уже выявила земли героев и гробницы павших.",
        // шипящие (ч/щ/ш) + свистящие (ц/з) + глухие (п/т/к/х) + звонкие (в/р/л/з/г/м) + р-л: врач/рощи/лечил/глаз
        "Врач у рощи лечил прыткую ящерицу, щуря хитрый глаз в шуме грозы.",
        // звонкие (д/б/в/з/л/г) + глухие (с/х/т/к/п) + шипящие (ч/щ) + р-л: дятел/берёзы/лось/бродил
        "Дятел долбил сухую ветку берёзы, а лось чавкал щавелем в чаще.",
    ];

    /// <summary>Phrase by index with a bounds check (index maps to the fixed prompt).</summary>
    public static string Phrase(int index) =>
        index >= 0 && index < Phrases.Count
            ? Phrases[index]
            : throw new ArgumentOutOfRangeException(nameof(index), "Phrase index is out of range (0..4).");
}
