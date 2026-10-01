using TOLIK.Contracts;

namespace TOLIK.Core;

/// <summary>
/// LLM task decomposition (§47, phase 2): turns a natural-language request into
/// structured <see cref="AgentStepSyntax"/> steps. Pure logic — model access is an
/// injected func (mirrors <see cref="PlanExecutionGuard"/>), so every failure
/// mode is unit-testable. Any failure returns null: callers fall back to the
/// deterministic single-step plan. Model output is untrusted data: steps are
/// sanitized and bounded here, and still gated per-step at execute time.
/// </summary>
public sealed class LlmTaskPlanner
{
    /// <summary>Longest spec the executor accepts (run_codex/run_opencode); longer steps are dropped.</summary>
    public const int MaxStepLength = 4608;

    public async Task<IReadOnlyList<string>?> TryDecomposeAsync(
        string request,
        int maxSteps,
        Func<string, CancellationToken, Task<string>> completeAsync,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request) || maxSteps < 1 || completeAsync is null) return null;
        string raw;
        try { raw = await completeAsync(BuildPrompt(request, maxSteps), cancellationToken); }
        catch { return null; }
        var steps = ParseSteps(raw)
            .Select(BoundedPlanner.SanitizeStep)
            .Where(a => !string.IsNullOrWhiteSpace(a) && a.Length <= MaxStepLength)
            .Take(maxSteps)
            .ToArray();
        return steps.Length == 0 ? null : steps;
    }

    public static string BuildPrompt(string request, int maxSteps) =>
        "Ты — планировщик задач ассистента TOLIK. Разбей просьбу пользователя на шаги. " +
        "Ответ — ТОЛЬКО JSON-массив строк, без пояснений и без markdown. " +
        "Каждый шаг: \"<команда>:<аргумент>\", команда строго из списка: " +
        string.Join(" ", AgentStepSyntax.Prefixes) +
        $" Не выдумывай команды. Максимум {maxSteps} шагов." +
        $" Просьба: {request.Trim()}";

    internal static IReadOnlyList<string> ParseSteps(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start >= 0 && end > start)
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(raw.Substring(start, end - start + 1));
                if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var fromJson = document.RootElement.EnumerateArray()
                        .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToArray();
                    if (fromJson.Length > 0) return fromJson;
                }
            }
            catch (System.Text.Json.JsonException) { }
        }
        // Small models often ignore the JSON instruction and emit bare/numbered
        // lines, sometimes inventing commands. Keep only lines starting with a
        // known prefix — the executor gates the rest anyway.
        return raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(StripLineMarker)
            .Where(line => AgentStepSyntax.Prefixes.Any(prefix =>
                line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static string StripLineMarker(string line)
    {
        var clean = line.Trim().Trim('`', '"', '\'').Trim();
        if (clean.Length > 1 && (clean[0] is '-' or '*' or '>')) clean = clean[1..].Trim();
        var dot = clean.IndexOf('.');
        if (dot is > 0 and <= 3 && clean[..dot].All(char.IsDigit)) clean = clean[(dot + 1)..].Trim();
        return clean;
    }
}
