namespace TOLIK.Contracts;

/// <summary>
/// The bounded closed-loop verification step (CU-3 wiring): the syntax is
/// <c>verify_gui:<target-spec>;expect=<substring></c> — the target part is a
/// well-formed <see cref="GuiTargetSpec"/> (semantic addressing only), the expectation
/// is a literal substring the post-action read must contain (ClosedLoopRunner.Verify
/// semantics: Verified = contains, Mismatch = the step fails honestly). NO wildcards
/// in the expectation (a Contains check — wildcards are meaningless and a regex would
/// be an unbounded surface), length-capped, control-char free, fail closed.
/// </summary>
public sealed record VerifyGuiStep(GuiTargetSpec.Target Target, string Expectation)
{
    public const int MaxExpectationLength = 256;

    /// <summary>Well-formedness — the Core's dispatch gate. Pure, deterministic, fail closed.</summary>
    public static bool IsWellFormed(string? step) => TryParse(step) is not null;

    /// <summary>Parses the step; null when malformed (the caller fails closed).</summary>
    public static VerifyGuiStep? TryParse(string? step)
    {
        if (string.IsNullOrWhiteSpace(step)) return null;

        var separator = step.IndexOf(';');
        if (separator <= 0 || separator == step.Length - 1) return null;

        var target = GuiTargetSpec.TryParse(step[..separator].Trim());
        if (target is null) return null; // the target must be a well-formed GuiTargetSpec

        var expectationPart = step[(separator + 1)..].Trim();
        if (!expectationPart.StartsWith("expect=", StringComparison.Ordinal)) return null;
        var expectation = expectationPart["expect=".Length..].Trim();
        if (expectation.Length == 0 || expectation.Length > MaxExpectationLength) return null;
        if (expectation.Any(char.IsControl) || expectation.Contains('*') || expectation.Contains('?')) return null;

        return new VerifyGuiStep(target, expectation);
    }
}
