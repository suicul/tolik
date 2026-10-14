using TOLIK.Contracts;
using TOLIK.Infrastructure.Authorization;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// CU-5 activation: the skill recorder turns COMPLETED gui task runs into workflow
/// skills. The invariants: ONLY verified workflows become skills (an act must be
/// paired with its verify — blind is not a skill); the identity is the stable hash of
/// the step sequence (the same workflow → the same skill, so the confidence ladder is
/// real); a first success adds at 0.5, a repeat success +0.1, a failure −0.2 — and a
/// failure of an UNKNOWN workflow records NOTHING (a single failed run never creates
/// a skill). Skills are DATA — they never authorize anything.
/// </summary>
public sealed class SkillRecorderTests
{
    private static readonly string[] ClosedLoopSteps =
    [
        "gui_click:automation_id=btnSubmit",
        "verify_gui:automation_id=statusLabel;expect=Saved"
    ];

    [Fact]
    public void Paired_click_and_verify_become_one_workflow_step()
    {
        var skill = SkillRecorder.FromGuiSteps("Submit the form", ClosedLoopSteps);
        Assert.NotNull(skill);
        Assert.Equal("windows", skill.Application);
        Assert.Equal(SkillProvenance.SuccessfulUse, skill.Provenance);
        Assert.Equal(SkillStatus.Active, skill.Status);
        Assert.Equal(SkillRecorder.InitialConfidence, skill.Confidence);
        var step = Assert.Single(skill.Workflow);
        Assert.Equal("gui_click", step.Verb);
        Assert.Equal("automation_id=btnSubmit", step.SemanticTarget);
        Assert.Equal("Saved", step.VerifyExpectation);
        Assert.Equal(GuiActionRisk.Medium, step.Risk);
    }

    [Fact]
    public void Unpaired_click_is_not_a_skill_blind_is_not_a_skill()
    {
        Assert.Null(SkillRecorder.FromGuiSteps("blind click", ["gui_click:automation_id=btn"]));
    }

    [Fact]
    public void Lone_verification_or_read_is_not_a_workflow()
    {
        Assert.Null(SkillRecorder.FromGuiSteps("lone verify", ["verify_gui:automation_id=x;expect=y"]));
        Assert.Null(SkillRecorder.FromGuiSteps("lone read", ["gui_read:automation_id=x"]));
    }

    [Fact]
    public void Mixed_non_gui_steps_are_not_a_pure_gui_workflow()
    {
        Assert.Null(SkillRecorder.FromGuiSteps("mixed", [
            "read_file:C:\\temp\\x.txt",
            "gui_click:automation_id=btn",
            "verify_gui:automation_id=x;expect=y"
        ]));
    }

    [Fact]
    public void Empty_sequence_is_not_a_skill()
    {
        Assert.Null(SkillRecorder.FromGuiSteps("empty", []));
    }

    [Fact]
    public void Identity_is_stable_across_runs_and_distinct_across_workflows()
    {
        var a1 = SkillRecorder.SkillId(ClosedLoopSteps);
        var a2 = SkillRecorder.SkillId(ClosedLoopSteps);
        Assert.Equal(a1, a2); // the same workflow → the same skill (the ladder is real)
        Assert.Equal(16, a1.Length); // the short stable hash

        var different = SkillRecorder.SkillId([
            "gui_click:automation_id=otherButton",
            "verify_gui:automation_id=statusLabel;expect=Saved"
        ]);
        Assert.NotEqual(a1, different);
    }

    [Fact]
    public void Confidence_ladder_first_success_adds_repeat_rises_failure_drops()
    {
        var store = new SkillStore();

        // The first success ADDS the skill at 0.5 (SuccessfulUse).
        Assert.True(SkillRecorder.RecordOutcome(store, "Submit the form", ClosedLoopSteps, success: true));
        var skill = store.TryGet(SkillRecorder.WindowsApplication, SkillRecorder.SkillId(ClosedLoopSteps));
        Assert.NotNull(skill);
        Assert.Equal(0.5, skill.Confidence);

        // A repeat success +0.1 (the ladder is real; the precision guard is for the
        // floating-point accumulation of the small deltas).
        Assert.True(SkillRecorder.RecordOutcome(store, "Submit the form", ClosedLoopSteps, success: true));
        Assert.Equal(0.6, store.TryGet(SkillRecorder.WindowsApplication, SkillRecorder.SkillId(ClosedLoopSteps))!.Confidence, 10);

        // A failure −0.2 (the store clamps 0..1).
        Assert.True(SkillRecorder.RecordOutcome(store, "Submit the form", ClosedLoopSteps, success: false));
        Assert.Equal(0.4, store.TryGet(SkillRecorder.WindowsApplication, SkillRecorder.SkillId(ClosedLoopSteps))!.Confidence, 10);
    }

    [Fact]
    public void Failure_of_an_unknown_workflow_records_nothing()
    {
        var store = new SkillStore();
        // A single failed run NEVER creates a skill — the store stays empty.
        Assert.False(SkillRecorder.RecordOutcome(store, "Failed run", ClosedLoopSteps, success: false));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Confidence_respects_the_store_clamps()
    {
        var store = new SkillStore();
        // Drive the skill to the cap: 0.5 + 0.1*5 = 1.0, and one more success stays clamped.
        for (var i = 0; i < 6; i++)
            SkillRecorder.RecordOutcome(store, "Submit the form", ClosedLoopSteps, success: true);
        Assert.Equal(1.0, store.TryGet(SkillRecorder.WindowsApplication, SkillRecorder.SkillId(ClosedLoopSteps))!.Confidence, 10);

        // Drive it to the floor: 1.0 − 0.2*5 = 0.0, and one more failure stays clamped.
        for (var i = 0; i < 6; i++)
            SkillRecorder.RecordOutcome(store, "Submit the form", ClosedLoopSteps, success: false);
        Assert.Equal(0.0, store.TryGet(SkillRecorder.WindowsApplication, SkillRecorder.SkillId(ClosedLoopSteps))!.Confidence, 10);
        // Below the trust threshold — never trusted for autonomous after the failures.
        Assert.False(store.IsTrustedForAutonomous(SkillRecorder.WindowsApplication, SkillRecorder.SkillId(ClosedLoopSteps)));
    }

    [Fact]
    public void Disable_kills_trust_even_at_full_confidence()
    {
        var store = new SkillStore();
        // Drive the skill to the cap: fully trusted.
        for (var i = 0; i < 6; i++)
            SkillRecorder.RecordOutcome(store, "Submit the form", ClosedLoopSteps, success: true);
        var id = SkillRecorder.SkillId(ClosedLoopSteps);
        Assert.True(store.IsTrustedForAutonomous(SkillRecorder.WindowsApplication, id));

        // The owner disables it: the status flips to Disabled → NEVER trusted again,
        // regardless of the confidence. (The management surface for mis-learned skills.)
        Assert.True(store.Disable(SkillRecorder.WindowsApplication, id));
        Assert.Equal(SkillStatus.Disabled, store.TryGet(SkillRecorder.WindowsApplication, id)!.Status);
        Assert.False(store.IsTrustedForAutonomous(SkillRecorder.WindowsApplication, id));

        // Re-disabling is a no-op success (the status is already Disabled).
        Assert.True(store.Disable(SkillRecorder.WindowsApplication, id));

        // An unknown skill: refused.
        Assert.False(store.Disable(SkillRecorder.WindowsApplication, "nosuchskill"));
    }
}
