namespace TOLIK.Core;

/// <summary>
/// One bounded adjustment a recipe makes: a named ExpressionState field + a delta.
/// The FIELD NAMES are the fixed ExpressionState whitelist (never free-form — the
/// validation rejects anything else); the DELTAS are clamped [-0.5..+0.5].
/// </summary>
public sealed record ExpressionRecipeDelta(string Field, double Delta);

/// <summary>
/// ExpressionRecipe (CE-5 — PLAN REFORM PRG-2c, директива §14): a DECLARATIVE
/// description of an expression — «сухая насмешка» is a combination of parameter
/// deltas (asymmetry, attention, pulses...), NEVER an animation clip and NEVER code.
/// The recipe applies as ADDITIVE DELTAS to the ExpressionState BEFORE the composer
/// (the one-blended-expression principle holds: the recipe is another influence,
/// blended like everything else). The LLM may PROPOSE recipes (data); the
/// deterministic validation gates them; the application is deterministic — the LLM
/// never executes anything in the renderer (директива §14: bounded, validated, safe).
/// Provenance is mandatory (Source); versions are monotonic (a higher version of the
/// same id replaces, a lower is rejected — the recipe history stays auditable).
/// </summary>
public sealed record ExpressionRecipe(
    string Id,
    int Version,
    string Name,
    string Context,
    string Source,
    double Confidence,
    IReadOnlyList<ExpressionRecipeDelta> Deltas)
{
    public bool IsValid => ExpressionRecipeValidator.Validate(this).IsOk;
}

/// <summary>The validation verdict: ok, or every reason it failed (fail-closed).</summary>
public sealed record RecipeValidation(bool IsOk, IReadOnlyList<string> Errors);

/// <summary>
/// The deterministic recipe validator (директива §14: "проверь допустимые диапазоны"
/// BEFORE an expression ever renders): the fixed field whitelist, the delta bounds,
/// the size caps, the provenance requirements. NEVER trusts the caller — everything
/// fails closed with the explicit reasons. Unit-tested; the LLM cannot bypass it.
/// </summary>
public static class ExpressionRecipeValidator
{
    /// <summary>The fixed DSL field whitelist — the ExpressionState's blendable fields
    /// (the exact PascalCase names; anything else is rejected, never guessed).</summary>
    public static readonly IReadOnlySet<string> Fields = new HashSet<string>(StringComparer.Ordinal)
    {
        "Amusement", "Curiosity", "Frustration", "Confidence", "Uncertainty",
        "Attention", "Engagement"
    };

    public const double MaxAbsDelta = 0.5;
    public const int MaxDeltas = 16;
    public const int MaxNameLength = 64;
    public const int MaxContextLength = 256;
    public const int MaxSourceLength = 128;

    public static RecipeValidation Validate(ExpressionRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(recipe.Id) || recipe.Id.Length > 64)
            errors.Add("id: required, ≤64 chars (fixed literal ids, never user content).");
        if (recipe.Version < 1)
            errors.Add("version: ≥1 required.");
        if (string.IsNullOrWhiteSpace(recipe.Name) || recipe.Name.Length > MaxNameLength)
            errors.Add($"name: required, ≤{MaxNameLength} chars.");
        if (string.IsNullOrWhiteSpace(recipe.Context) || recipe.Context.Length > MaxContextLength)
            errors.Add($"context: required, ≤{MaxContextLength} chars (the usage context).");
        if (string.IsNullOrWhiteSpace(recipe.Source) || recipe.Source.Length > MaxSourceLength)
            errors.Add($"source: required provenance, ≤{MaxSourceLength} chars (who/what created this).");
        if (!double.IsFinite(recipe.Confidence) || recipe.Confidence is < 0.0 or > 1.0)
            errors.Add("confidence: [0..1] finite required.");
        if (recipe.Deltas is null || recipe.Deltas.Count == 0)
        {
            errors.Add("deltas: at least one adjustment required (an empty recipe does nothing).");
        }
        else
        {
            if (recipe.Deltas.Count > MaxDeltas)
                errors.Add($"deltas: ≤{MaxDeltas} adjustments.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var delta in recipe.Deltas)
            {
                if (string.IsNullOrWhiteSpace(delta?.Field) || !Fields.Contains(delta.Field))
                    errors.Add($"deltas: unknown field \"{delta?.Field}\" (the fixed whitelist only — never free-form).");
                else if (!seen.Add(delta.Field))
                    errors.Add($"deltas: duplicate field \"{delta.Field}\".");
                if (delta is not null && (!double.IsFinite(delta.Delta) || Math.Abs(delta.Delta) > MaxAbsDelta))
                    errors.Add($"deltas[{delta.Field}]: |delta| ≤ {MaxAbsDelta}, finite (fail closed).");
            }
        }
        return new RecipeValidation(errors.Count == 0, errors);
    }
}

/// <summary>
/// The pure recipe application (CE-5): applies the validated deltas to an
/// ExpressionState ADDITIVELY — the composer then blends the adjusted state with
/// everything else (the one-blended-expression principle). Deterministic; the
/// Normalized() clamps the final values; a null recipe = the unchanged state.
/// </summary>
public static class RecipeApplication
{
    public static Contracts.ExpressionState Apply(Contracts.ExpressionState state, ExpressionRecipe? recipe)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (recipe is null || recipe.Deltas.Count == 0) return state;
        var s = state;
        foreach (var delta in recipe.Deltas)
        {
            // The whitelist is enforced by the validator; the application still guards
            // (the defense in depth — never trusts, even a stored recipe).
            if (!ExpressionRecipeValidator.Fields.Contains(delta.Field) ||
                !double.IsFinite(delta.Delta) || Math.Abs(delta.Delta) > ExpressionRecipeValidator.MaxAbsDelta)
                continue;
            s = delta.Field switch
            {
                "Amusement" => s with { Amusement = s.Amusement + delta.Delta },
                "Curiosity" => s with { Curiosity = s.Curiosity + delta.Delta },
                "Frustration" => s with { Frustration = s.Frustration + delta.Delta },
                "Confidence" => s with { Confidence = s.Confidence + delta.Delta },
                "Uncertainty" => s with { Uncertainty = s.Uncertainty + delta.Delta },
                "Attention" => s with { Attention = s.Attention + delta.Delta },
                "Engagement" => s with { Engagement = s.Engagement + delta.Delta },
                _ => s
            };
        }
        return s.Normalized(); // the final clamp: garbage never renders as a spike
    }
}
