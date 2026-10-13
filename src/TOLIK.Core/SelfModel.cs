using TOLIK.Contracts;

namespace TOLIK.Core;

/// <summary>
/// SelfModel (docs/mind.md MI-8 — PLAN REFORM PRG-5b): what TOLIK honestly knows
/// about itself — the registered capabilities (from the registry), the current mind
/// state, and the STRUCTURAL limits (the fixed honest list). The SelfModel ≠
/// Memory ≠ Skill ≠ Task ≠ Authorization (mind.md §5): it is a READ-ONLY
/// description built from the real sources, never an authority — the type carries
/// no decision surface, and the limits are FIXED literals (the mind cannot edit
/// its own limits — the anti-self-escalation is structural).
/// </summary>
public sealed record SelfModel(
    DateTimeOffset BuiltAt,
    IReadOnlyList<(string Capability, string Risk)> Capabilities,
    MindSnapshot Mind,
    int RunningTasks,
    IReadOnlyList<string> Limits)
{
    /// <summary>The FIXED structural limits — the mind cannot edit them (the
    /// anti-self-escalation is structural: no API mutates this list).</summary>
    public static readonly IReadOnlyList<string> StructuralLimits =
    [
        "authorization: identity → policy → confirmation — never bypassed by mood or personality",
        "emotions: never read by the authorization pipeline (both directions)",
        "personality: phrases the verdicts, never alters them",
        "occupations: selected from the fixed catalog; the task creation routes through the authorization",
        "rewards: provenance mandatory; they inform, never grant",
        "proactive: never interrupts; the daily budget is hard",
        "locked/emergency: the owner-only states, the mind rests, never wanders"
    ];

    public static IReadOnlyList<(string Capability, string Risk)> DescribeCapabilities()
    {
        var capabilities = new List<(string, string)>();
        foreach (var descriptor in CapabilityRegistry.All)
            capabilities.Add((descriptor.Id.ToString(), descriptor.DeclaredRisk.ToString()));
        return capabilities;
    }
}

/// <summary>
/// SelfModelBuilder (MI-8): builds the honest self-model from the real sources —
/// the capability registry + the mind snapshot + the running-task count. PURE,
/// deterministic, read-only (§50: nothing fabricated; the model IS the state).
/// </summary>
public static class SelfModelBuilder
{
    public static SelfModel Build(MindSnapshot mind, int runningTasks, DateTimeOffset now) =>
        new(now,
            SelfModel.DescribeCapabilities(),
            mind,
            runningTasks,
            SelfModel.StructuralLimits);
}

/// <summary>
/// The Mind Inspector (docs/mind.md MI-8 — PLAN REFORM PRG-5b): the explainable
/// activity selection — WHY this occupation was chosen. A STRUCTURED TRACE of the
/// real decision inputs (the drive levels, the candidate utilities, the
/// restrictions), NOT chain-of-thought. Deterministic; read-only.
/// </summary>
public sealed record SelectionTraceEntry(
    string Activity,
    double Utility,
    bool CooldownBlocked,
    bool RestrictedByState,
    bool Selected);

public sealed record SelectionTrace(
    string Selected,
    string? Summary,
    IReadOnlyList<SelectionTraceEntry> Candidates,
    DateTimeOffset At)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Selected);
}

/// <summary>
/// The Inspector's pure evaluation (MI-8): evaluates EVERY candidate in the catalog
/// against the current drives — the utility + why it was blocked (the cooldown/the
/// state restriction) — WITHOUT mutating the cooldown state (a read-only trace;
/// the real selection still runs in the MindRuntime's Tick). The trace explains the
/// decision; it never makes one.
/// </summary>
public static class SelectionInspector
{
    public static SelectionTrace Trace(
        ActivitySelector selector,
        IReadOnlyList<DriveState> drives,
        DateTimeOffset now,
        bool locked,
        bool emergencyStopped)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(drives);

        var byName = drives.GroupBy(d => d.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Intensity, StringComparer.Ordinal);
        var entries = new List<SelectionTraceEntry>();
        var selected = ActivityNames.Idle;
        var selectedSummary = ActivityCandidate.Idle.Summary;
        foreach (var candidate in ActivityCandidate.Catalog)
        {
            var utility = candidate.Name == ActivityNames.Idle
                ? Math.Max(0.15, byName.GetValueOrDefault(DriveNames.Rest, 0.0))
                : candidate.DriveAffinities.Sum(a => byName.GetValueOrDefault(a, 0.0));
            var cooldownBlocked = !locked && !emergencyStopped && candidate.Name != ActivityNames.Idle &&
                selector.IsCoolingDown(candidate.Name, now);
            var restricted = (locked && candidate.Name is not (ActivityNames.Idle or ActivityNames.SystemReview)) ||
                             (emergencyStopped && candidate.Name != ActivityNames.Idle);
            var isWinner = !restricted && !cooldownBlocked &&
                utility > (entries.Count > 0 ? entries.Max(e => e.Utility) : 0.0);
            if (isWinner)
            {
                selected = candidate.Name;
                selectedSummary = candidate.Summary;
            }
            entries.Add(new SelectionTraceEntry(candidate.Name, utility, cooldownBlocked, restricted, isWinner));
        }
        return new SelectionTrace(selected, selectedSummary, entries, now);
    }
}
