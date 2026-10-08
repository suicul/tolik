using System.Text.RegularExpressions;

namespace TOLIK.Core;

public enum ChatIntentKind
{
    Conversation,       // обычный разговор → модель
    OpenApp,            // открой X → RunProcess через Агента
    Reminder,            // напомни через N … → ProactiveStore
    StoreMemory,        // запомни: X → DurableMemoryStore
    StoreFamilyMeme,    // это наш семейный мем: X → humor + family lore
    StoreFunny,         // это было смешно, запомни → humor
    RecallMemory,        // что я говорил про X / помнишь X → поиск по памяти
    BuildProject,       // собери проект X → RunBuild через Агента
    TestProject,        // запусти тесты X → RunTests через Агента
    SwitchVoiceMode,     // ночной/игровой/обычный/тактический режим → VoiceRuntimeState
    EnrollTrustedPerson  // запомни голос <name> / это моя <relation> <name> → voice enrollment (VI-4)
}

public sealed record ChatIntent(ChatIntentKind Kind, string Argument);

/// <summary>
/// Deterministic intent classification for the chat MVP.
/// Rules are explicit, auditable and never rely on the LLM (§24: security-critical
/// logic is deterministic). Unknown input → Conversation (model decides).
/// </summary>
public static partial class IntentRouter
{
    [GeneratedRegex(@"^открой\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex OpenAppRegex();

    [GeneratedRegex(@"^напомни\s+(?:через\s+)?(\d+)\s*(сек|с|мин|м|час|ч)\w*\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ReminderRegex();

    [GeneratedRegex(@"^запомни(?:\s*,|\s*что|\s*--)?\s*(?:это\s+)?(?:наш\s+семейный\s+мем)?\s*[:：]?\s*(.+)$|^запомни\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex StoreMemoryRegex();

    [GeneratedRegex(@"^(?:что|о\s+чём)\s+(?:я\s+)?(?:говорил|говорила)?\s*(?:про|о)\s+(.+?)\??$|^(?:помнишь|вспомни)\s+(.+?)\??$", RegexOptions.IgnoreCase)]
    private static partial Regex RecallRegex();

    [GeneratedRegex(@"^(?:собери|построй)\s+(?:проект\s+)?(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex BuildRegex();

    [GeneratedRegex(@"^(?:запусти\s+)?(?:тесты|тест)\s+(?:для\s+|в\s+|проекта\s+)?(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TestRegex();

    [GeneratedRegex(@"(?:это\s+было\s+смешно|было\s+смешно|это\s+смешно|ржу|смеюсь)", RegexOptions.IgnoreCase)]
    private static partial Regex FunnyRegex();

    // --- VI-4: conversational trusted-person enrollment (docs/voice-identity.md §5) ---
    // Kinship term list shared by both enrollment patterns (const concat keeps
    // GeneratedRegex source generation happy).
    private const string RelationTerms =
        @"жен[ауы]|муж[ауы]?|мам[ауы]|пап[ауы]|сын[ауы]?|дочь|дочк[ауи]|брат[ауы]?|сестр[ауы]|бабушк[ауи]|дедушк[ауи]|тёт[яуи]|дяд[яюи]|друг[ауы]?|подруг[ауи]|свекровь|тещ[ауи]|племянник\w*|родственник\w*";

    // «это моя <relation> <name>» — the kinship term + a spoken name are required: a bare
    // relation ("это моя мама") or a possessive without one ("это моя машина") must stay
    // Conversation, so the relation form only classifies with a name tail present.
    [GeneratedRegex(@"(?:это\s+)?(?:мо[яю]|моего|моей|наш\w*)\s*(?:" + RelationTerms + @")\s+(?<name>\S.*)", RegexOptions.IgnoreCase)]
    private static partial Regex EnrollRelationRegex();

    // «запомни голос <name>» / «запомни голос» (name optional → Argument empty → the
    // caller asks). The end-anchored tail keeps "запомни голосовой пароль" out of the
    // enrollment path (only whitespace continues the marker).
    [GeneratedRegex(@"запомни\s+голос(?:\s+\S.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex EnrollVoiceNameRegex();

    // «запомни её/его голос» (plan §5: «это Мария, запомни её голос») — with or without
    // a spoken subject before the request clause.
    [GeneratedRegex(@"запомни\s+(?:е[ё]|его)\s+голос", RegexOptions.IgnoreCase)]
    private static partial Regex EnrollVoicePronounRegex();

    // Spoken-subject prefix inside the extracted target ("моя жена Мария" → "Мария";
    // "моя мама" → "" — nothing name-like left → the caller asks).
    [GeneratedRegex(@"(?:мо[яю]|моего|моей|наш\w*)?\s*(?:" + RelationTerms + @")\s*", RegexOptions.IgnoreCase)]
    private static partial Regex EnrollSubjectPrefixRegex();

    public static ChatIntent Classify(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return new(ChatIntentKind.Conversation, string.Empty);
        return ClassifyPatterns(Normalize(message));
    }

    /// <summary>
    /// Classifies literal text (§22/T11): no wake-word normalization and no implicit
    /// "открой <app>" voice convenience — used for sanitizing MODEL output, where
    /// the model's words stay data and only text that literally matches an executable
    /// intent pattern is instruction-shaped.
    /// </summary>
    public static ChatIntent ClassifyLiteral(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return new(ChatIntentKind.Conversation, string.Empty);
        return ClassifyPatterns(message.Trim());
    }

    /// <summary>Shared deterministic pattern checks (explicit, auditable, never the LLM).</summary>
    private static ChatIntent ClassifyPatterns(string text)
    {
        // "запомни, это наш семейный мем: X" — до StoreMemory (специфичнее)
        if (text.Contains("семейный мем", StringComparison.OrdinalIgnoreCase))
        {
            var meme = ExtractAfter(text, "семейный мем");
            if (meme.Length > 0) return new(ChatIntentKind.StoreFamilyMeme, meme);
        }

        if (FunnyRegex().IsMatch(text))
            return new(ChatIntentKind.StoreFunny, text);

        if (OpenAppRegex().Match(text) is { Success: true } open)
            return new(ChatIntentKind.OpenApp, open.Groups[1].Value.Trim());

        if (ReminderRegex().Match(text) is { Success: true } reminder)
            return new(ChatIntentKind.Reminder, $"{reminder.Groups[1].Value}|{reminder.Groups[2].Value}|{reminder.Groups[3].Value.Trim()}");

        if (RecallRegex().Match(text) is { Success: true } recall)
            return new(ChatIntentKind.RecallMemory, (recall.Groups[1].Success ? recall.Groups[1].Value : recall.Groups[2].Value).Trim());

        if (BuildRegex().Match(text) is { Success: true } build)
            return new(ChatIntentKind.BuildProject, build.Groups[1].Value.Trim());

        if (TestRegex().Match(text) is { Success: true } test)
            return new(ChatIntentKind.TestProject, test.Groups[1].Value.Trim());

        if (text.Contains("ночной режим", StringComparison.OrdinalIgnoreCase) || text.Equals("ночь", StringComparison.OrdinalIgnoreCase))
            return new(ChatIntentKind.SwitchVoiceMode, "Night");
        // Silent (voice-runtime-v2.md M8): the manual mute — TOLIK listens, answers
        // as text, never speaks.
        if (text.Contains("тихий режим", StringComparison.OrdinalIgnoreCase) || text.Contains("тишина", StringComparison.OrdinalIgnoreCase) || text.Equals("молчи", StringComparison.OrdinalIgnoreCase))
            return new(ChatIntentKind.SwitchVoiceMode, "Silent");
        if (text.Contains("игровой режим", StringComparison.OrdinalIgnoreCase))
            return new(ChatIntentKind.SwitchVoiceMode, "Gaming");
        if (text.Contains("режим", StringComparison.OrdinalIgnoreCase) && text.Contains("обычн", StringComparison.OrdinalIgnoreCase))
            return new(ChatIntentKind.SwitchVoiceMode, "Normal");

        // Voice enrollment of a trusted person (docs/voice-identity.md §5, VI-4): checked
        // BEFORE StoreMemory ("запомни ...") — more specific first, like семейный мем.
        if (TryClassifyEnrollTrustedPerson(text) is { } enroll)
            return enroll;

        if (StoreMemoryRegex().Match(text) is { Success: true } store)
            return new(ChatIntentKind.StoreMemory, store.Groups[1].Value.Trim());

        return new(ChatIntentKind.Conversation, text);
    }

    /// <summary>
    /// Strips wake-word addressing ("Толик, ...", "толик ...") and trailing punctuation
    /// so voice commands like "Толик, открой калькулятор" still match intent patterns.
    /// Also handles the case where user says just the app name after wake word ("Толик, калькулятор").
    /// </summary>
    private static string Normalize(string message)
    {
        var text = message.Trim();
        foreach (var prefix in new[] { "толик,", "толик", "толик!", "толик.", "толь, ", "tolik," })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].TrimStart(' ', ',');
                break;
            }
        }
        text = text.TrimEnd('.', '!', '?');

        // Single-word smalltalk ("привет", "спасибо", "пока" …) must stay
        // Conversation: without this guard every lone word becomes "открой X"
        // and hits RunProcess (observed live: "привет" → start process 'привет').
        if (!text.Contains(' ', StringComparison.Ordinal) && IsSmalltalk(text))
            return text;

        // Single-word mode commands ("ночь", "молчи", "тишина") stay literal — they
        // must hit the SwitchVoiceMode patterns, never the implied app open
        // (latent bug this fixes: "ночь" became "открой ночь" and matched OpenApp,
        // so the documented Night alias was unreachable through Classify).
        if (!text.Contains(' ', StringComparison.Ordinal) &&
            text.ToLowerInvariant() is "ночь" or "молчи" or "тишина")
            return text;

        // If the text is a single word (no spaces) and doesn't match any intent,
        // treat it as an implied "открой <app>" command for voice convenience.
        if (!text.Contains(' ', StringComparison.Ordinal) &&
            !OpenAppRegex().IsMatch(text) &&
            !ReminderRegex().IsMatch(text) &&
            !RecallRegex().IsMatch(text) &&
            !StoreMemoryRegex().IsMatch(text) &&
            !BuildRegex().IsMatch(text) &&
            !TestRegex().IsMatch(text) &&
            !text.Contains("семейный мем", StringComparison.OrdinalIgnoreCase) &&
            !FunnyRegex().IsMatch(text))
        {
            // Implicit "открой <app>" for single-word app names after wake word
            text = "открой " + text;
        }
        
        return text;
    }

    /// <summary>Single-word smalltalk never means "open an app".</summary>
    private static bool IsSmalltalk(string text) => text.ToLowerInvariant() switch
    {
        "привет" or "здравствуй" or "здравствуйте" or "приветствую" or "хай" or "салют" or "здарова" or "йо" or "ку"
            or "спасибо" or "благодарю" or "пожалуйста" or "пока"
            or "да" or "нет" or "ок" or "окей" or "хорошо" or "понятно" or "ага" or "угу" or "ладно"
            or "hello" or "hi" or "hey" or "thanks" or "thank" or "bye" or "yes" or "no" or "ok" or "okay" or "good" => true,
        _ => false,
    };

    // --- VI-4: conversational trusted-person enrollment (docs/voice-identity.md §5) ---

    /// <summary>
    /// Deterministic EnrollTrustedPerson classification (docs/voice-identity.md §5, VI-4).
    /// Patterns: «запомни голос <name>», «это моя <relation> <name>»,
    /// «запомни её/его голос». "запомни голос" без name → Argument empty → the caller
    /// asks. Null when nothing enrollment-shaped matched — the caller falls through to
    /// the normal classification (a bare relation or a verb tail is speech, not enrollment).
    /// </summary>
    private static ChatIntent? TryClassifyEnrollTrustedPerson(string text)
    {
        // «запомни голос <name>» / «запомни голос» (без name → Argument empty → caller asks).
        if (EnrollVoiceNameRegex().IsMatch(text))
        {
            var marker = "запомни голос";
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            var direct = text[(index + marker.Length)..].Trim();
            if (direct.Length > 0)
                return new(ChatIntentKind.EnrollTrustedPerson, ExtractSubjectName(direct));
            // "…, запомни голос" — a spoken subject may precede the marker; extract it,
            // otherwise the Argument stays empty (the caller asks).
            var subject = index > 0 ? text[..index].TrimEnd(' ', ',').Trim() : string.Empty;
            return new(ChatIntentKind.EnrollTrustedPerson, ExtractSubjectName(subject));
        }
        // «запомни её/его голос» (plan §5: «это Мария, запомни её голос») — with or
        // without a spoken subject before the request clause.
        if (EnrollVoicePronounRegex().IsMatch(text))
        {
            var cut = text.IndexOf("запомни", StringComparison.OrdinalIgnoreCase);
            var subject = cut > 0 ? text[..cut].TrimEnd(' ', ',').Trim() : string.Empty;
            return new(ChatIntentKind.EnrollTrustedPerson, ExtractSubjectName(subject));
        }
        // «это моя <relation> <name>» — the kinship term + a spoken name; a bare relation
        // ("это моя мама") or a verb tail ("мама сидит рядом") is speech, not enrollment
        // → null (falls through to the normal classification).
        if (EnrollRelationRegex().Match(text) is { Success: true } relation)
        {
            var raw = relation.Groups["name"].Value;
            var cut = raw.IndexOf("запомни", StringComparison.OrdinalIgnoreCase);
            var name = NormalizeTargetName(cut >= 0 ? raw[..cut] : raw, maxWords: 2);
            return name.Length == 0 ? null : new(ChatIntentKind.EnrollTrustedPerson, name);
        }
        return null;
    }

    /// <summary>Strips the spoken addressing ("это", possessive + kinship term) from a
    /// spoken subject and bounds the remainder to a plausible name (≤2 words, not a verb
    /// tail; a lone possessive pronoun is not a name). Empty when nothing name-like
    /// remains — the caller asks (Argument empty).</summary>
    private static string ExtractSubjectName(string subject)
    {
        var value = subject.Trim();
        if (value.StartsWith("это", StringComparison.OrdinalIgnoreCase))
            value = value[3..].TrimStart(' ', ',').Trim();
        if (EnrollSubjectPrefixRegex().Match(value) is { Success: true } prefix && prefix.Length > 0)
            value = value[prefix.Length..].TrimStart(' ', ',').Trim();
        return NormalizeTargetName(value, maxWords: 2);
    }

    /// <summary>Bounds a captured name: at most <paramref name="maxWords"/> words (a name
    /// is 1–2 words; longer tails are speech — the caller asks instead), not a verb tail.</summary>
    private static string NormalizeTargetName(string value, int maxWords)
    {
        var cleaned = value.Trim(' ', ',', '-', '—', ':');
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 1 || words.Length > maxWords) return string.Empty;
        if (IsCommonVerb(words[0])) return string.Empty;
        if (words.Length == 1 && IsPossessivePronoun(words[0])) return string.Empty;
        return cleaned;
    }

    /// <summary>Common present-tense verbs — "мама сидит рядом" is speech, not a name tail.</summary>
    private static bool IsCommonVerb(string word) => word.ToLowerInvariant() switch
    {
        "сидит" or "стоит" or "работает" or "живёт" or "живет" or "говорит" or "любит" or "спит"
            or "умерла" or "родилась" or "пришла" or "ушла" or "приехала" or "уехала" or "готовит"
            or "звонит" or "позвонит" or "болеет" or "лежит" or "ждёт" or "ждет" => true,
        _ => false,
    };

    /// <summary>A lone possessive pronoun is not a name (no kinship term and no name spoken).</summary>
    private static bool IsPossessivePronoun(string word) => word.ToLowerInvariant() switch
    {
        "моя" or "мой" or "мою" or "моего" or "моей" or "наша" or "наш" or "наши" or "наших" => true,
        _ => false,
    };

    private static string ExtractAfter(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return string.Empty;
        var rest = text[(index + marker.Length)..].TrimStart(':', ' ', ',', '-', '—');
        return rest.Trim();
    }
}
