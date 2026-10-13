namespace TOLIK.Core;

/// <summary>The store record: the recipe + the honest usage statistics (директива §15).</summary>
public sealed record RecipeRecord(
    ExpressionRecipe Recipe,
    int UsageCount,
    int FeedbackPositive,
    int FeedbackNegative,
    DateTimeOffset LastVerifiedAt);

/// <summary>
/// RecipeStore (CE-5 — PLAN REFORM PRG-2c): the versioned expression-recipe store with
/// the mandatory provenance and the honest usage/feedback statistics. Bounded (128
/// recipes; a full store REJECTS — never silently drops); the versions are monotonic
/// (the same id: a strictly higher version replaces, an equal/lower is rejected —
/// the recipe history stays auditable); every addition passes the deterministic
/// validation (the LLM may propose; the validation decides — §14). Thread-safe.
/// The store is DATA + bookkeeping only: nothing here authorizes anything, and the
/// recipes render — they never act.
/// </summary>
public sealed class RecipeStore
{
    public const int Capacity = 128;

    private readonly object _gate = new();
    private readonly Dictionary<string, RecipeRecord> _recipes = new(StringComparer.Ordinal);

    public ExpressionRecipe? ActiveRecipe { get { lock (_gate) { return _active; } } }
    private ExpressionRecipe? _active;

    /// <summary>Adds a validated recipe. Returns (ok, errors). The versioning:
    /// a new id stores; the same id needs a strictly higher version to replace.</summary>
    public RecipeValidation Add(ExpressionRecipe recipe, DateTimeOffset? at = null)
    {
        var validation = ExpressionRecipeValidator.Validate(recipe);
        if (!validation.IsOk) return validation;
        lock (_gate)
        {
            if (_recipes.TryGetValue(recipe.Id, out var existing))
            {
                if (recipe.Version <= existing.Recipe.Version)
                    return new RecipeValidation(false, [$"version: {recipe.Version} must be strictly higher than the stored {existing.Recipe.Version}."]);
                _recipes[recipe.Id] = existing with { Recipe = recipe, LastVerifiedAt = at ?? DateTimeOffset.UtcNow };
                if (_active?.Id == recipe.Id) _active = recipe; // the active slot follows the update
                return validation;
            }
            if (_recipes.Count >= Capacity)
                return new RecipeValidation(false, [$"capacity: the store is full ({Capacity}) — the owner curates."]);
            _recipes[recipe.Id] = new RecipeRecord(recipe, 0, 0, 0, at ?? DateTimeOffset.UtcNow);
            return validation;
        }
    }

    public RecipeRecord? TryGet(string id)
    {
        lock (_gate) { return _recipes.TryGetValue(id, out var record) ? record : null; }
    }

    public IReadOnlyList<RecipeRecord> List()
    {
        lock (_gate) { return _recipes.Values.ToList(); }
    }

    /// <summary>Activates a recipe (the expression blends its deltas while active).
    /// Counts as one usage — the honest activation statistic. False = unknown id.</summary>
    public bool TryActivate(string id)
    {
        lock (_gate)
        {
            if (!_recipes.TryGetValue(id, out var record)) return false;
            _active = record.Recipe;
            _recipes[id] = record with { UsageCount = record.UsageCount + 1 };
            return true;
        }
    }

    public void Deactivate()
    {
        lock (_gate) { _active = null; }
    }

    /// <summary>Records the owner's feedback (директива §15: Feedback, the real signal
    /// for CE-6's habit formation — never reward-hacking: the feedback shapes the
    /// EXPRESSIONS only, never any permission).</summary>
    public bool RecordFeedback(string id, bool positive)
    {
        lock (_gate)
        {
            if (!_recipes.TryGetValue(id, out var record)) return false;
            _recipes[id] = positive
                ? record with { FeedbackPositive = record.FeedbackPositive + 1 }
                : record with { FeedbackNegative = record.FeedbackNegative + 1 };
            return true;
        }
    }
}
