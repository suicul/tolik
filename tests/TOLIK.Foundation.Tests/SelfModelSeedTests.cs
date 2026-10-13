using TOLIK.Contracts;
using TOLIK.Core;
using TOLIK.Infrastructure.Authorization;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// MI-8 Self-model + Mind Inspector (docs/mind.md MI-8 — PLAN REFORM PRG-5b):
/// the HONEST self-description from the real sources (the capabilities + the mind
/// state + the FIXED structural limits — the mind cannot edit its own limits, the
/// anti-self-escalation is structural); the Inspector's explainable selection trace
/// (the structured decision inputs, NOT chain-of-thought; read-only, never mutates
/// the cooldowns).
/// </summary>
public sealed class SelfModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_model_lists_all_registered_capabilities_with_risks()
    {
        var model = SelfModelBuilder.Build(
            new MindSnapshot(MindState.SnapshotVersion, 0.5, 0, 0, 0, 0, 0, 0, "LOW", null, null, T0),
            runningTasks: 2, now: T0);
        Assert.Equal(CapabilityRegistry.All.Length, model.Capabilities.Count);
        Assert.Contains(model.Capabilities, c => c.Capability == "WriteFile" && c.Risk == "Modify");
        Assert.Contains(model.Capabilities, c => c.Capability == "InstallTolikUpdate" && c.Risk == "Critical");
    }

    [Fact]
    public void Structural_limits_are_fixed_and_cannot_be_edited()
    {
        Assert.Contains(SelfModel.StructuralLimits, l => l.Contains("authorization", StringComparison.Ordinal));
        Assert.Contains(SelfModel.StructuralLimits, l => l.Contains("emotions", StringComparison.Ordinal));
        Assert.Contains(SelfModel.StructuralLimits, l => l.Contains("rewards", StringComparison.Ordinal));
        Assert.Contains(SelfModel.StructuralLimits, l => l.Contains("locked/emergency", StringComparison.Ordinal));
        // The list is IReadOnlyList on a static readonly field — no API mutates it
        // (the anti-self-escalation is structural: the mind cannot edit its own limits).
    }

    [Fact]
    public void The_model_carries_the_real_mind_state_and_limits()
    {
        var mind = new MindSnapshot(MindState.SnapshotVersion, 0.7, 0.3, 0, 0, 0.2, 0.1, 0.1, "FOCUSED", null, null, T0);
        var model = SelfModelBuilder.Build(mind, runningTasks: 2, now: T0);
        Assert.Equal(0.7, model.Mind.Boredom, 6);        // the real state flows
        Assert.Equal(2, model.RunningTasks);             // the real work
        Assert.Equal(7, model.Limits.Count);             // the FIXED structural limits
    }
}

public sealed class SelectionInspectorTests
{
    [Fact]
    public void The_trace_explains_why_the_occupation_was_chosen()
    {
        var selector = new ActivitySelector();
        var drives = new[]
        {
            new DriveState(DriveNames.Curiosity, 0.9), new DriveState(DriveNames.Novelty, 0.7),
            new DriveState(DriveNames.Exploration, 0.6), new DriveState(DriveNames.Rest, 0.1)
        };
        var trace = SelectionInspector.Trace(selector, drives,
            new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero), locked: false, emergencyStopped: false);
        Assert.Equal(ActivityNames.Investigation, trace.Selected);
        Assert.True(trace.Candidates.Count >= 5); // all the catalog candidates evaluated
        var investigation = trace.Candidates.First(c => c.Activity == ActivityNames.Investigation);
        Assert.True(investigation.Utility > 0.5);  // the curiosity drives score high
        Assert.True(investigation.Selected);
    }

    [Fact]
    public void The_trace_is_read_only_never_mutates_the_cooldowns()
    {
        var selector = new ActivitySelector();
        var drives = new[]
        {
            new DriveState(DriveNames.Curiosity, 0.9), new DriveState(DriveNames.Novelty, 0.7),
            new DriveState(DriveNames.Exploration, 0.6), new DriveState(DriveNames.Rest, 0.1)
        };
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        _ = SelectionInspector.Trace(selector, drives, now, locked: false, emergencyStopped: false); // twice — the cooldowns untouched
        // The real selection still picks the same winner: the trace did NOT consume it.
        var selection = selector.Select(drives, now, locked: false);
        Assert.Equal(ActivityNames.Investigation, selection.Activity);
    }

    [Fact]
    public void The_trace_shows_the_restrictions()
    {
        var selector = new ActivitySelector();
        var drives = new[]
        {
            new DriveState(DriveNames.Curiosity, 1.0), new DriveState(DriveNames.Maintenance, 0.9),
            new DriveState(DriveNames.Safety, 0.9), new DriveState(DriveNames.Order, 0.8)
        };
        var trace = SelectionInspector.Trace(selector, drives,
            new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero), locked: true, emergencyStopped: false);
        var investigation = trace.Candidates.First(c => c.Activity == ActivityNames.Investigation);
        Assert.True(investigation.RestrictedByState); // LOCKED restricts — visible in the trace
        Assert.Equal(ActivityNames.SystemReview, trace.Selected); // the safety-relevant winner
    }
}

/// <summary>
/// MI-9-seed (docs/mind.md MI-9 — PLAN REFORM PRG-5b): the Genesis seed MECHANISM —
/// the data loads pre-ceremony, the ACTIVATION flips only at the ceremony's
/// touchpoint (§85), the mind reads the seed ONLY when activated (the pre-Genesis
/// honest boundary); the seed carries NO capability/permission fields (structurally:
/// GenesisSeedData has temperament + values only); the re-activation after birth is
/// refused (mind.md §7 continuity: the identity is born once).
/// </summary>
public sealed class GenesisSeedTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static GenesisSeedData ValidSeed() => new(
        "calm-technical-dry-humor", ["Точность важнее скорости", "Не лгать даже мелочь"], T0);

    [Fact]
    public void Lifecycle_load_seeded_activate()
    {
        var seed = new GenesisSeedRuntime();
        Assert.Equal(SeedStatus.NotSeeded, seed.Status);
        seed.Load(ValidSeed());
        Assert.Equal(SeedStatus.Seeded, seed.Status);   // loaded — NOT activated (pre-Genesis)
        seed.Activate();                                 // the ceremony's touchpoint
        Assert.Equal(SeedStatus.Activated, seed.Status);
        Assert.NotNull(seed.Snapshot());                 // the mind reads the seed AFTER activation
        Assert.Equal("calm-technical-dry-humor", seed.Snapshot()!.Temperament);
    }

    [Fact]
    public void The_mind_cannot_read_the_seed_before_activation()
    {
        var seed = new GenesisSeedRuntime();
        seed.Load(ValidSeed());
        Assert.Equal(SeedStatus.Seeded, seed.Status);
        Assert.Null(seed.Snapshot()); // pre-Genesis: the mind CANNOT read the seed (the honest boundary)
        seed.Activate();
        Assert.NotNull(seed.Snapshot());
    }

    [Fact]
    public void The_activation_without_a_seed_fails()
    {
        var seed = new GenesisSeedRuntime();
        Assert.Throws<InvalidOperationException>(seed.Activate); // never born without the ceremony's seed
    }

    [Fact]
    public void Invalid_seed_data_fails_closed()
    {
        var seed = new GenesisSeedRuntime();
        Assert.Throws<ArgumentException>(() => seed.Load(new GenesisSeedData("", [], T0)));                       // no temperament
        Assert.Throws<ArgumentException>(() => seed.Load(new GenesisSeedData("ok", [], T0)));                    // no values
        Assert.Throws<ArgumentException>(() => seed.Load(new GenesisSeedData(new string('a', 300), ["x"], T0))); // overlong
    }

    [Fact]
    public void The_seed_data_carries_no_permissions_structurally()
    {
        // The reflection over GenesisSeedData: NO capability/permission/policy fields —
        // the seed INFORMS the personality, it can never grant (the structural
        // anti-self-escalation, tested like the phrasing and the reward tests).
        var propertyNames = typeof(GenesisSeedData).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("Temperament", propertyNames);
        Assert.Contains("CoreValues", propertyNames);
        Assert.DoesNotContain(propertyNames, n => n is "Permissions" or "Capabilities" or "Role" or "TrustLevel");
    }

    [Fact]
    public void The_seed_is_born_once_continuity_holds()
    {
        var seed = new GenesisSeedRuntime();
        seed.Load(ValidSeed());
        seed.Activate();
        // The re-seed after the birth is REFUSED (mind.md §7: the identity continuity —
        // the born entity is not replaced by another seed).
        Assert.Throws<InvalidOperationException>(() => seed.Load(ValidSeed()));
    }
}
