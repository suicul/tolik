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
    SwitchVoiceMode     // ночной/игровой/обычный/тактический режим → VoiceRuntimeState
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
        if (text.Contains("игровой режим", StringComparison.OrdinalIgnoreCase))
            return new(ChatIntentKind.SwitchVoiceMode, "Gaming");
        if (text.Contains("режим", StringComparison.OrdinalIgnoreCase) && text.Contains("обычн", StringComparison.OrdinalIgnoreCase))
            return new(ChatIntentKind.SwitchVoiceMode, "Normal");

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

    private static string ExtractAfter(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return string.Empty;
        var rest = text[(index + marker.Length)..].TrimStart(':', ' ', ',', '-', '—');
        return rest.Trim();
    }
}
