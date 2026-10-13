namespace TOLIK.Core;

/// <summary>
/// PersonalityPhrasing (docs/governance.md §6, Phase F — PLAN REFORM PRG-4e):
/// **Personality ≠ Authorization** (треб. #16/#17 — КРИТИЧНО). The personality
/// controls HOW something is said; the security layer decides WHAT is allowed.
/// The phrasing receives the ALREADY-MADE verdict and wraps it in human language:
/// a DENY is spoken THROUGH the personality («Не, это за пределами моих полномочий,
/// и это не обсуждается») — but the decision is deterministic and the personality
/// NEVER bypasses it. **Emotion ≠ permissions in BOTH directions**: this module's
/// inputs are verdicts + descriptions, its output is a STRING — the type carries
/// no authority; the AutonomyContext structurally has no emotional fields (tested).
/// The v1 templates are FIXED deterministic phrasings (a human tone even fully
/// offline); the LLM may phrase richer LATER, always on top of the same verdicts.
/// </summary>
public static class PersonalityPhrasing
{
    /// <summary>The human phrasing of a DENY — the deterministic decision UNDERNEATH;
    /// the personality never softens, never reinterprets, never bypasses the verdict.
    /// The `amusement` tone modifier (0 = neutral, > 0 = playful) comes from the mind's
    /// active recipe — it changes the TONE, never the DECISION (Personality ≠
    /// Authorization: the phrasing wraps, the boundary stands).</summary>
    public static string Deny(string resource, string declaredRisk, double amusement = 0.0)
    {
        if (string.IsNullOrWhiteSpace(resource)) throw new ArgumentException("Resource is required.", nameof(resource));
        var basePhrase = $"Не, «{resource}» — за пределами моих полномочий, и это не обсуждается.";
        return amusement > 0.3
            ? $"{basePhrase} 😏 (Риск: {declaredRisk}.)"
            : $"{basePhrase} (Риск: {declaredRisk}.)";
    }

    /// <summary>
    /// The human confirmation request (треб. #19: a human in-the-loop, never
    /// «CONFIRM ACTION ID 84729»). The technical bound-confirmation mechanism
    /// (SHA-256, single-use, 10-min) stays underneath UNCHANGED — this is the
    /// PHRASING only; the substance (the action, the target, the risk, the
    /// consequence) comes from the existing confirmation record.
    /// </summary>
    public static string ConfirmationRequest(string action, string resource, string risk, string consequence)
    {
        if (string.IsNullOrWhiteSpace(action)) throw new ArgumentException("Action is required.", nameof(action));
        if (string.IsNullOrWhiteSpace(resource)) throw new ArgumentException("Resource is required.", nameof(resource));
        return $"Ща, надо твоё подтверждение: {action} — {resource}. Риск {risk}: {consequence}. Подтвердишь?";
    }

    /// <summary>The human task-result notification (never the bare corporate status).</summary>
    public static string TaskResult(bool success, string summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) throw new ArgumentException("Summary is required.", nameof(summary));
        return success
            ? $"Готово: {summary}"
            : $"Есть проблема: {summary}. Разберусь — но глянь, если срочно.";
    }

    /// <summary>The human stall/pause explanation (CA-2's L3 — stop + preserve + ask).</summary>
    public static string TaskPausedForStall(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Reason is required.", nameof(reason));
        return $"Задачу поставил на паузу: {reason}. Никаких потерь — всё сохранено.";
    }
}
