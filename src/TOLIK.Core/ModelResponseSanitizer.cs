using System.Text.RegularExpressions;

namespace TOLIK.Core;

public sealed record ModelSanitizationResult(string Text, IReadOnlyList<string> RemovedInstructions);

/// <summary>
/// Sanitizes model responses (§22/T11, MVP-IMPLEMENTATION step 5): the model is an
/// untrusted external provider (T16) — its output stays data, never authority. Lines
/// that classify as an executable intent (IntentRouter patterns, the single source of
/// executable instruction forms) are stripped: the model answers, it never commands.
/// Prose survives; only instruction-shaped lines go, and they are reported back for
/// transparency. Known tradeoff: a legitimately quoted instruction-shaped line in a
/// normal answer is also stripped (fail closed for the user).
/// </summary>
public static partial class ModelResponseSanitizer
{
    [GeneratedRegex(@"^(?:толик|tolik)\s*[,!]", RegexOptions.IgnoreCase)]
    private static partial Regex TolikAddressedRegex();

    public static ModelSanitizationResult Sanitize(string? modelText)
    {
        if (string.IsNullOrWhiteSpace(modelText)) return new(modelText ?? string.Empty, []);
        var lines = modelText.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        var removed = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Instruction-shaped: the model addressing itself ("Толик, ...") or a line
            // that literally matches an executable intent pattern (no voice convenience
            // normalization — the model's words stay data, §22/T11).
            if (trimmed.Length > 0 &&
                (TolikAddressedRegex().IsMatch(trimmed) || IntentRouter.ClassifyLiteral(trimmed).Kind != ChatIntentKind.Conversation))
                removed.Add(trimmed);
            else
                kept.Add(line);
        }
        return new(string.Join('\n', kept), removed.ToArray());
    }
}
