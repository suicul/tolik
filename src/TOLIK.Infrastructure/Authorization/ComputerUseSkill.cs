using TOLIK.Contracts;

namespace TOLIK.Infrastructure.Authorization;

/// <summary>The GUI action risk tiers (docs/computer-use.md §5, CU-3 — треб. #10).</summary>
public enum GuiActionRisk
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>One typed computer-use action step (CU-3): the SEMANTIC target (the role + the AutomationId/text from the IUiObserver — never raw coordinates as the primary), the action verb, the risk tier, and the closed-loop verification (the expected semantic state after the action).</summary>
public sealed record ComputerUseAction(
    string Verb,
    string SemanticTarget,
    string? TextInput,
    GuiActionRisk Risk,
    string VerifyExpectation)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Verb) && !string.IsNullOrWhiteSpace(SemanticTarget) &&
        !string.IsNullOrWhiteSpace(VerifyExpectation);
}

public enum ClosedLoopStepStatus
{
    Pending,
    Authorized,
    Executed,
    Verified,
    Mismatch,
    Failed,
    Stopped
}

/// <summary>The bounded closed-loop runner: max 3 retries per step, then STOP → explain → ask the owner (never an infinite click-loop).</summary>
public static class ClosedLoopRunner
{
    public const int MaxAttempts = 3;

    public static ClosedLoopStepStatus Verify(ComputerUseAction action, IUiObserver? observedAfter, string? actualSemanticState)
    {
        if (string.IsNullOrWhiteSpace(actualSemanticState))
            return ClosedLoopStepStatus.Mismatch;
        return actualSemanticState.Contains(action.VerifyExpectation, StringComparison.Ordinal)
            ? ClosedLoopStepStatus.Verified
            : ClosedLoopStepStatus.Mismatch;
    }

    public static ClosedLoopStepStatus AfterMismatch(int attempt, int maxAttempts = MaxAttempts) =>
        attempt >= maxAttempts ? ClosedLoopStepStatus.Stopped : ClosedLoopStepStatus.Pending;
}

/// <summary>One semantic workflow step: the verb, the semantic role+id target (NEVER coordinates), the optional typed text, the risk tier, and the closed-loop verification.</summary>
public sealed record SkillWorkflowStep(
    string Verb,
    string SemanticTarget,
    string? TextInput,
    GuiActionRisk Risk,
    string VerifyExpectation);

public enum SkillStatus
{
    PendingOwnerApproval,
    Active,
    Disabled
}

public enum SkillProvenance
{
    Exploration,
    Demonstration,
    SuccessfulUse
}

/// <summary>A semantic skill: the workflows as SEMANTIC step sequences (role/automation_id/text, NEVER coordinates), the recovery procedures, and the provenance (learned_by).</summary>
public sealed record ComputerUseSkill(
    string Id,
    string Application,
    string AppVersion,
    int Version,
    string Description,
    IReadOnlyList<SkillWorkflowStep> Workflow,
    SkillProvenance Provenance,
    SkillStatus Status,
    double Confidence)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Application) &&
        Version >= 1 && Workflow.Count is >= 1 and <= 64 &&
        Workflow.All(s => !string.IsNullOrWhiteSpace(s.Verb) && !string.IsNullOrWhiteSpace(s.SemanticTarget) &&
                          !string.IsNullOrWhiteSpace(s.VerifyExpectation));
}

/// <summary>The bounded (64) versioned skill store: the semantic skills as DATA (they never authorize anything — the authorization pipeline stays mandatory); the confidence lifecycle (LOW_CONFIDENCE → no risky autonomous); thread-safe.</summary>
public sealed class SkillStore
{
    public const int Capacity = 64;
    public const double TrustedThreshold = 0.6;

    private readonly object _gate = new();
    private readonly Dictionary<string, ComputerUseSkill> _skills = new(StringComparer.Ordinal);

    public (bool Ok, string? Error) Add(ComputerUseSkill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (!skill.IsValid) return (false, "skill: invalid.");
        var key = $"{skill.Application}:{skill.Id}";
        lock (_gate)
        {
            if (_skills.TryGetValue(key, out var existing) && skill.Version <= existing.Version)
                return (false, $"version: {skill.Version} must be strictly higher than {existing.Version}.");
            if (_skills.Count >= Capacity && !_skills.ContainsKey(key))
                return (false, $"capacity: full ({Capacity}).");
            _skills[key] = skill;
            return (true, null);
        }
    }

    public ComputerUseSkill? TryGet(string application, string id)
    {
        lock (_gate) { return _skills.TryGetValue($"{application}:{id}", out var skill) ? skill : null; }
    }

    public IReadOnlyList<ComputerUseSkill> List()
    {
        lock (_gate) { return _skills.Values.ToList(); }
    }

    public bool AdjustConfidence(string application, string id, double delta)
    {
        if (!double.IsFinite(delta)) return false;
        lock (_gate)
        {
            var key = $"{application}:{id}";
            if (!_skills.TryGetValue(key, out var skill)) return false;
            _skills[key] = skill with { Confidence = Math.Clamp(skill.Confidence + delta, 0.0, 1.0) };
            return true;
        }
    }

    public bool IsTrustedForAutonomous(string application, string id)
    {
        var skill = TryGet(application, id);
        return skill is not null && skill.Status == SkillStatus.Active && skill.Confidence >= TrustedThreshold;
    }

    public bool Disable(string application, string id)
    {
        lock (_gate)
        {
            var key = $"{application}:{id}";
            if (!_skills.TryGetValue(key, out var skill)) return false;
            _skills[key] = skill with { Status = SkillStatus.Disabled };
            return true;
        }
    }
}
