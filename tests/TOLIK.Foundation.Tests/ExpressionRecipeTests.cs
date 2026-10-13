using TOLIK.Contracts;
using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// ExpressionRecipe DSL (CE-5 — PLAN REFORM PRG-2c, директива §14): the declarative
/// expressions — «сухая насмешка» is a combination of BOUNDED PARAMETER DELTAS,
/// never an animation clip and never code. The deterministic validation gates
/// everything (the fixed field whitelist, |delta| ≤ 0.5, provenance required — the
/// LLM may propose, the validation decides); the application blends ADDITIVELY
/// pre-composer (the one-blended-expression principle); the store versions strictly
/// monotonic per id; nothing here authorizes anything — recipes render, never act.
/// </summary>
public sealed class ExpressionRecipeTests
{
    private static readonly (string Field, double Delta)[] DefaultDeltas = [("Amusement", 0.2), ("Uncertainty", -0.1)];

    private static ExpressionRecipe ValidRecipe(params (string Field, double Delta)[] deltas) => new(
        "dry_smirk", 1, "Сухая насмешка", "sardistic reply", "owner_teaching", 0.7,
        (deltas.Length > 0 ? deltas : DefaultDeltas).Select(d => new ExpressionRecipeDelta(d.Field, d.Delta)).ToList());

    // ─────────────────────────── The validation (fail-closed) ───────────────────────────

    [Fact]
    public void Valid_recipe_passes()
    {
        var validation = ExpressionRecipeValidator.Validate(ValidRecipe());
        Assert.True(validation.IsOk);
        Assert.Empty(validation.Errors);
    }

    [Fact]
    public void Unknown_field_is_rejected_never_guessed()
    {
        var recipe = ValidRecipe(("EyeScale", 0.3)); // not in the whitelist — a composer output, not an input
        var validation = ExpressionRecipeValidator.Validate(recipe);
        Assert.False(validation.IsOk);
        Assert.Contains(validation.Errors, e => e.Contains("unknown field"));
    }

    [Fact]
    public void Out_of_range_delta_is_rejected()
    {
        var tooBig = ExpressionRecipeValidator.Validate(ValidRecipe(("Amusement", 0.6)));
        Assert.False(tooBig.IsOk);
        var nan = ExpressionRecipeValidator.Validate(ValidRecipe(("Curiosity", double.NaN)));
        Assert.False(nan.IsOk);
    }

    [Fact]
    public void Duplicate_fields_and_empty_deltas_are_rejected()
    {
        var dup = ExpressionRecipeValidator.Validate(ValidRecipe(("Amusement", 0.1), ("Amusement", 0.2)));
        Assert.False(dup.IsOk);
        Assert.Contains(dup.Errors, e => e.Contains("duplicate"));
        var empty = ExpressionRecipeValidator.Validate(ValidRecipe() with { Deltas = [] });
        Assert.False(empty.IsOk);
    }

    [Fact]
    public void Provenance_and_bounds_are_mandatory()
    {
        var noSource = ExpressionRecipeValidator.Validate(ValidRecipe() with { Source = "" });
        Assert.False(noSource.IsOk); // the provenance is required (директива §14/§15)
        var badConfidence = ExpressionRecipeValidator.Validate(ValidRecipe() with { Confidence = 1.5 });
        Assert.False(badConfidence.IsOk);
        var badVersion = ExpressionRecipeValidator.Validate(ValidRecipe() with { Version = 0 });
        Assert.False(badVersion.IsOk);
        var tooMany = ExpressionRecipeValidator.Validate(ValidRecipe(Enumerable.Range(0, 17).Select(i => ($"F{i}", 0.1)).ToArray()));
        Assert.False(tooMany.IsOk); // ≤16 adjustments
    }

    // ─────────────────────── The additive application (the blend) ───────────────────────

    [Fact]
    public void Deltas_blend_additively_into_the_expression_state()
    {
        var baseState = new ExpressionState(
            Attention: 0.5, Thinking: 0, Concentration: 0,
            Amusement: 0.1, Curiosity: 0, Frustration: 0, Confidence: 0, Uncertainty: 0.3,
            Engagement: 1, IsSpeaking: false, Amplitude: 0, LowBand: 0, MidBand: 0, HighBand: 0,
            SignalLevel: SystemSignalLevel.None, Processing: 0, TaskProgress: 0, Recovery: 0, Locked: false);
        var adjusted = RecipeApplication.Apply(baseState, ValidRecipe(("Amusement", 0.3), ("Uncertainty", -0.1)));
        Assert.Equal(0.4, adjusted.Amusement, 6);  // 0.1 + 0.3 — the additive blend
        Assert.Equal(0.2, adjusted.Uncertainty, 6); // 0.3 − 0.1
        // The untouched fields stay.
        Assert.Equal(0.5, adjusted.Attention, 6);
        Assert.Equal(1.0, adjusted.Engagement, 6);
    }

    [Fact]
    public void Application_clamps_and_ignores_garbage()
    {
        var high = new ExpressionState(
            Attention: 0, Thinking: 0, Concentration: 0,
            Amusement: 0.9, Curiosity: 0, Frustration: 0, Confidence: 0, Uncertainty: 0,
            Engagement: 0, IsSpeaking: false, Amplitude: 0, LowBand: 0, MidBand: 0, HighBand: 0,
            SignalLevel: SystemSignalLevel.None, Processing: 0, TaskProgress: 0, Recovery: 0, Locked: false);
        var clamped = RecipeApplication.Apply(high, ValidRecipe(("Amusement", 0.5)));
        Assert.Equal(1.0, clamped.Amusement, 6); // clamped — never a spike

        // The defense in depth: an invalid delta in a stored recipe is SKIPPED in the application.
        var defensive = RecipeApplication.Apply(high, ValidRecipe() with { Deltas = [new("Hacked", 0.5)] });
        Assert.Equal(0.9, defensive.Amusement, 6); // untouched — the whitelist holds even inside

        // A null recipe changes nothing.
        Assert.Equal(high, RecipeApplication.Apply(high, null));
    }

    [Fact]
    public void The_active_recipe_reaches_the_composed_expression()
    {
        var recipe = ValidRecipe(("Amusement", 0.4), ("Curiosity", 0.3));
        var withRecipe = TOLIK.Core.Host.CephalonExpressionService.ComposeFor(
            new TOLIK.Core.Host.CephalonUserSnapshot(0, 0), locked: false, turnInFlight: false, connected: true,
            new MindEmotions(0, 0, 0, 0), voice: null, activeRecipe: recipe, previous: null, deltaSeconds: 1.0);
        var without = TOLIK.Core.Host.CephalonExpressionService.ComposeFor(
            new TOLIK.Core.Host.CephalonUserSnapshot(0, 0), locked: false, turnInFlight: false, connected: true,
            new MindEmotions(0, 0, 0, 0), voice: null, activeRecipe: null, previous: null, deltaSeconds: 1.0);
        // The amusement rises → the playful pulse; the curiosity rises → the exploratory rotation: DIFFERENT frames.
        Assert.NotEqual(without.PulseAmplitude, withRecipe.PulseAmplitude, 6);
        Assert.NotEqual(without.Rotation, withRecipe.Rotation, 6);
    }

    // ─────────────────────────── The store (versioned, honest) ───────────────────────────

    [Fact]
    public void Store_versions_strictly_monotonic_per_id()
    {
        var store = new RecipeStore();
        Assert.True(store.Add(ValidRecipe()).IsOk);
        Assert.False(store.Add(ValidRecipe()).IsOk);                          // the same version — rejected
        Assert.False(store.Add(ValidRecipe() with { Version = 0 }).IsOk);    // lower — rejected
        Assert.True(store.Add(ValidRecipe() with { Version = 2 }).IsOk);      // higher — replaces
        Assert.Equal(2, store.TryGet("dry_smirk")!.Recipe.Version);
    }

    [Fact]
    public void Activation_counts_usage_and_unknown_ids_fail()
    {
        var store = new RecipeStore();
        Assert.True(store.Add(ValidRecipe()).IsOk);
        Assert.False(store.TryActivate("nonexistent"));
        Assert.True(store.TryActivate("dry_smirk"));
        Assert.Equal(1, store.TryGet("dry_smirk")!.UsageCount); // the honest activation statistic
        Assert.NotNull(store.ActiveRecipe);
        store.Deactivate();
        Assert.Null(store.ActiveRecipe);
    }

    [Fact]
    public void Feedback_records_and_capacity_is_bounded()
    {
        var store = new RecipeStore();
        Assert.True(store.Add(ValidRecipe()).IsOk);
        Assert.True(store.RecordFeedback("dry_smirk", positive: true));
        Assert.True(store.RecordFeedback("dry_smirk", positive: false));
        var record = store.TryGet("dry_smirk")!;
        Assert.Equal(1, record.FeedbackPositive);
        Assert.Equal(1, record.FeedbackNegative);
        Assert.False(store.RecordFeedback("ghost", true)); // the honest unknown

        for (var i = 0; i < RecipeStore.Capacity - 1; i++) // dry_smirk already occupies one slot
            Assert.True(store.Add(ValidRecipe() with { Id = $"r{i}" }).IsOk);
        Assert.Equal(RecipeStore.Capacity, store.List().Count);
        Assert.False(store.Add(ValidRecipe() with { Id = "overflow" }).IsOk); // the full store REJECTS — never silently drops
    }

    [Fact]
    public void Active_recipe_follows_the_version_update()
    {
        var store = new RecipeStore();
        Assert.True(store.Add(ValidRecipe()).IsOk);
        Assert.True(store.TryActivate("dry_smirk"));
        var v2 = ValidRecipe(("Amusement", 0.45)) with { Version = 2 };
        Assert.True(store.Add(v2).IsOk);
        Assert.Equal(2, store.ActiveRecipe!.Version);       // the active slot follows the update
        Assert.Equal(0.45, store.ActiveRecipe.Deltas[0].Delta, 6);
    }
}
