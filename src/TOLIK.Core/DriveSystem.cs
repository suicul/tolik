namespace TOLIK.Core;

/// <summary>
/// DriveSystem (docs/mind.md §4, MI-3 — PLAN REFORM PRG-1c): the 15 named drives,
/// each a deterministic intensity [0..1] computed from REAL measurable inputs only
/// (the MindState, the interaction clock, the active tasks, the security state).
/// «Boredom создаёт МОТИВАЦИЮ, не permission» — the drives feed the ActivitySelector;
/// every action still passes the authorization pipeline (governance). Drives without
/// a direct source derive from the boredom (the MI-2 seeking-activity level) scaled
/// by their configured weight — honest (the boredom IS measurable), never fabricated.
/// TOLIK_DRIVE_* config weights validated fail-closed (positive finite else default).
/// </summary>
public sealed record DriveState(string Name, double Intensity)
{
    public bool IsValid => double.IsFinite(Intensity) && Intensity is >= 0.0 and <= 1.0;
}

/// <summary>The real inputs the drives read (supplied by the MindRuntime — no DB/Lock access here).</summary>
public sealed record MindInputs(
    double MinutesSinceInteraction,
    int ActiveTasks,
    bool Locked);

public static class DriveNames
{
    public const string Interaction = "Interaction";
    public const string Curiosity = "Curiosity";
    public const string Novelty = "Novelty";
    public const string Completion = "Completion";
    public const string Helpfulness = "Helpfulness";
    public const string Exploration = "Exploration";
    public const string Learning = "Learning";
    public const string Creativity = "Creativity";
    public const string Maintenance = "Maintenance";
    public const string SelfImprovement = "SelfImprovement";
    public const string SocialConnection = "SocialConnection";
    public const string Rest = "Rest";
    public const string Safety = "Safety";
    public const string Order = "Order";
    public const string ProblemSolving = "ProblemSolving";
}

public static class DriveSystem
{
    /// <summary>Computes all 15 drives deterministically from the real inputs.</summary>
    public static IReadOnlyList<DriveState> Compute(MindSnapshot state, MindInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(inputs);

        var boredom = Finite01(state.Boredom);
        var social = Finite01(state.SocialDrive);
        var curiosity = Finite01(state.Curiosity);
        var novelty = Finite01(state.Novelty);
        var load = Finite01(state.CognitiveLoad);
        var activity = ActivityLevelValue(state.ActivityLevel);

        // The boredom-derived drives: the seeking-activity level scaled by weight
        // (drives with no dedicated source — their honest channel is the boredom).
        var seeking = boredom;

        return
        [
            new(DriveNames.Interaction, Finite01(1.0 - Math.Min(inputs.MinutesSinceInteraction, 30.0) / 30.0)),
            new(DriveNames.Curiosity, curiosity),
            new(DriveNames.Novelty, novelty),
            new(DriveNames.Completion, inputs.ActiveTasks > 0 ? Finite01(0.5 + 0.1 * Math.Min(inputs.ActiveTasks, 5)) : 0.0),
            new(DriveNames.Helpfulness, Weighted(seeking, "HELPFULNESS")),
            new(DriveNames.Exploration, Weighted(seeking, "EXPLORATION")),
            new(DriveNames.Learning, Weighted(seeking, "LEARNING")),
            new(DriveNames.Creativity, Weighted(seeking, "CREATIVITY")),
            new(DriveNames.Maintenance, Weighted(Math.Max(seeking, load > 0.6 ? 0.5 : 0.0), "MAINTENANCE")),
            new(DriveNames.SelfImprovement, Weighted(seeking, "SELF_IMPROVEMENT")),
            new(DriveNames.SocialConnection, Finite01(0.3 * social + 0.7 * (1.0 - Math.Min(inputs.MinutesSinceInteraction, 120.0) / 120.0))),
            new(DriveNames.Rest, Finite01(1.0 - activity)), // REST-level calm raises the rest drive
            new(DriveNames.Safety, inputs.Locked ? 1.0 : Weighted(0.2, "SAFETY")), // LOCKED: the safety drive dominates
            new(DriveNames.Order, Weighted(seeking * 0.7 + (load > 0.8 ? 0.3 : 0.0), "ORDER")),
            new(DriveNames.ProblemSolving, inputs.ActiveTasks > 0 ? Finite01(0.4 + 0.15 * Math.Min(inputs.ActiveTasks, 4)) : Weighted(seeking, "PROBLEM_SOLVING")),
        ];
    }

    /// <summary>Weight lookup: TOLIK_DRIVE_<NAME> (positive finite else 1.0 — fail closed).</summary>
    private static double Weighted(double value, string name) =>
        Finite01(value * (double.TryParse(Environment.GetEnvironmentVariable("TOLIK_DRIVE_" + name), out var w) && double.IsFinite(w) && w > 0 ? w : 1.0));

    private static double ActivityLevelValue(string? level) => level switch
    {
        "REST" => 0.1,
        "LOW" => 0.3,
        "NORMAL" => 0.5,
        "FOCUSED" => 0.8,
        "INTENSE" => 1.0,
        _ => 0.1
    };

    private static double Finite01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;
}
