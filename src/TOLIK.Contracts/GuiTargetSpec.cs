namespace TOLIK.Contracts;

/// <summary>
/// The bounded GUI element target spec (CU-6 GUI actions v1): the ONLY addressing the
/// GUI capabilities accept. Semantic addressing — <c>automation_id=<id></c> or
/// <c>name=<text></c> — NEVER coordinates, never wildcards, never window handles
/// (SkillStore parity: skills address elements semantically; a coordinate click is a
/// different, unsandboxed thing that v1 does not offer). Length-capped, control-char
/// free, fail closed on anything else. The Core validates well-formedness before
/// dispatch; the Agent re-parses (defense in depth — the wire could carry anything).
/// </summary>
public static class GuiTargetSpec
{
    public const int MaxIdLength = 128;
    public const int MaxNameLength = 256;

    /// <summary>The parsed target: how to find the element and what to match.</summary>
    public sealed record Target(GuiTargetKind Kind, string Value);

    /// <summary>Well-formedness check — the ONLY syntax gate. Pure, deterministic,
    /// fail closed: anything not exactly one of the two bounded forms is rejected.</summary>
    public static bool IsWellFormed(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec) || spec.Length > MaxNameLength + 32) return false;
        return TryParse(spec) is not null;
    }

    /// <summary>Parses the spec; null when malformed (the caller fails closed).</summary>
    public static Target? TryParse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;

        var separator = spec.IndexOf('=');
        if (separator <= 0 || separator == spec.Length - 1) return null;

        var kindText = spec[..separator].Trim();
        var value = spec[(separator + 1)..].Trim();
        if (value.Length == 0) return null;

        // No control characters, no wildcards (the bounded syntax only).
        if (value.Any(char.IsControl) || value.Contains('*') || value.Contains('?')) return null;

        switch (kindText)
        {
            case "automation_id" when value.Length <= MaxIdLength:
                return new Target(GuiTargetKind.AutomationId, value);
            case "name" when value.Length <= MaxNameLength:
                return new Target(GuiTargetKind.Name, value);
            default:
                return null; // unknown kind or oversized value — fail closed
        }
    }
}

/// <summary>The bounded GUI element addressing kinds (CU-6 v1 — semantic only).</summary>
public enum GuiTargetKind
{
    /// <summary>Match AutomationId exactly (the SkillStore's preferred address).</summary>
    AutomationId,
    /// <summary>Match the element Name exactly (visible text — more brittle, still semantic).</summary>
    Name
}
