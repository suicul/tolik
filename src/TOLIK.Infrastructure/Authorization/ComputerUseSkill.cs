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

/// <summary>
/// CU-5 activation: turns COMPLETED gui task runs into workflow skills. The pairing
/// rule — an ACT (gui_click) followed by its VERIFY (verify_gui) becomes ONE workflow
/// step (the act carrying the verify's expectation); an unpaired act, a lone
/// verification, or any non-gui step means the run is NOT a skill (the honest
/// invariant: a skill is what TOLIK VERIFIED, never what it did blind). The identity
/// is the stable hash of the step sequence — the same workflow on repeated runs maps
/// to the same skill, so the confidence ladder is real: a first success adds the skill
/// at 0.5 (SuccessfulUse), a repeat success +0.1, a failure −0.2 (the store clamps
/// 0..1). Skills are DATA — they never authorize anything (the authorization pipeline
/// stays mandatory; IsTrustedForAutonomous gates future autonomous use).
/// </summary>
public static class SkillRecorder
{
    public const string WindowsApplication = "windows";
    public const double InitialConfidence = 0.5;
    public const double SuccessDelta = 0.1;
    public const double FailureDelta = -0.2;

    /// <summary>The stable workflow identity: SHA-256 of the exact step sequence (the
    /// same workflow → the same skill; a different sequence → a different skill).</summary>
    public static string SkillId(IReadOnlyList<string> stepActions)
    {
        ArgumentNullException.ThrowIfNull(stepActions);
        var material = string.Join("|", stepActions);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash)[..16];
    }

    /// <summary>Derives the workflow skill from a gui task run; null when the run is
    /// not a verifiable workflow (the caller records nothing — never a blind skill).</summary>
    public static ComputerUseSkill? FromGuiSteps(string taskDescription, IReadOnlyList<string> stepActions)
    {
        ArgumentNullException.ThrowIfNull(stepActions);
        if (stepActions.Count == 0) return null;

        var workflow = new List<SkillWorkflowStep>();
        for (var i = 0; i < stepActions.Count; i++)
        {
            var action = (stepActions[i] ?? string.Empty).Trim();
            if (action.StartsWith(AgentStepSyntax.GuiClick, StringComparison.OrdinalIgnoreCase))
            {
                // The act must be followed by its verification — otherwise the run
                // did something blind, and blind is not a skill.
                if (i + 1 >= stepActions.Count) return null;
                var next = (stepActions[i + 1] ?? string.Empty).Trim();
                if (!next.StartsWith(AgentStepSyntax.VerifyGui, StringComparison.OrdinalIgnoreCase)) return null;
                var verify = VerifyGuiStep.TryParse(next[AgentStepSyntax.VerifyGui.Length..].Trim());
                if (verify is null) return null;
                workflow.Add(new SkillWorkflowStep(
                    Verb: "gui_click",
                    SemanticTarget: action[AgentStepSyntax.GuiClick.Length..].Trim(),
                    TextInput: null,
                    Risk: GuiActionRisk.Medium, // GuiClick = ToolRiskLevel.Modify
                    VerifyExpectation: verify.Expectation));
                i++; // the verification was consumed as the act's pair
            }
            else
            {
                // A lone verify/read or any non-gui step: not a pure gui workflow —
                // v1 records only complete, verified workflows. Fail honest (null).
                return null;
            }
        }

        if (workflow.Count == 0) return null;
        var description = string.IsNullOrWhiteSpace(taskDescription)
            ? "gui workflow"
            : taskDescription.Trim()[..Math.Min(taskDescription.Trim().Length, MaxDescriptionLength)];
        return new ComputerUseSkill(
            Id: SkillId(stepActions),
            Application: WindowsApplication,
            AppVersion: "1",
            Version: 1,
            Description: description,
            Workflow: workflow,
            Provenance: SkillProvenance.SuccessfulUse,
            Status: SkillStatus.Active,
            Confidence: InitialConfidence);
    }

    private const int MaxDescriptionLength = 256;

    /// <summary>The confidence ladder. A first success ADDS the skill (0.5); a repeat
    /// success +0.1; a failure −0.2 — but a failure of an UNKNOWN workflow records
    /// nothing (there is nothing to demote; a single failed run never creates a skill).
    /// Returns true when the store was updated.</summary>
    public static bool RecordOutcome(SkillStore store, string taskDescription, IReadOnlyList<string> stepActions, bool success)
    {
        ArgumentNullException.ThrowIfNull(store);
        var skill = FromGuiSteps(taskDescription, stepActions);
        if (skill is null) return false;
        var existing = store.TryGet(WindowsApplication, skill.Id);
        if (existing is null)
            return success && store.Add(skill).Ok;
        return store.AdjustConfidence(WindowsApplication, skill.Id, success ? SuccessDelta : FailureDelta);
    }
}
