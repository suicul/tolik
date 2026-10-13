using TOLIK.Contracts;
using TOLIK.Infrastructure.Authorization;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// CU-3 Closed-loop + Verification + CU-5 Skill Learning (docs/computer-use.md
/// §5/§8 — PLAN REFORM PRG-6): every action is a CYCLE (act → observe → verify —
/// never click A→B→C without checking); the bounded retries (3 → STOP → explain →
/// ask); the GUI risk tiers feed the EXISTING AutonomyPolicy; the skills are
/// SEMANTIC (role+id, never coordinates) with the confidence lifecycle
/// (LOW_CONFIDENCE → no risky autonomous); the skills are DATA (they never
/// authorize anything — the pipeline stays mandatory).
/// </summary>
public sealed class ClosedLoopTests
{
    private static ComputerUseAction Click(string target) =>
        new("click", target, null, GuiActionRisk.Low, $"state:{target} visible");

    [Fact]
    public void Verified_when_the_observation_matches()
    {
        var action = Click("button:Отправить");
        Assert.Equal(ClosedLoopStepStatus.Verified,
            ClosedLoopRunner.Verify(action, null, "state:button:Отправить visible"));
    }

    [Fact]
    public void Mismatch_never_counts_as_success()
    {
        // Директива: «клик никогда не считается успешным» без проверки перехода состояния.
        var action = Click("button:Отправить");
        Assert.Equal(ClosedLoopStepStatus.Mismatch,
            ClosedLoopRunner.Verify(action, null, "state:search_results visible")); // expected chat, got search
        Assert.Equal(ClosedLoopStepStatus.Mismatch, ClosedLoopRunner.Verify(action, null, null)); // no observation
    }

    [Fact]
    public void Bounded_retries_then_stop_explain_ask()
    {
        // Мismatch → retry (bounded, MaxAttempts) → STOP → explain → ask owner (никогда бесконечный цикл).
        Assert.Equal(ClosedLoopStepStatus.Pending, ClosedLoopRunner.AfterMismatch(1, 3));
        Assert.Equal(ClosedLoopStepStatus.Pending, ClosedLoopRunner.AfterMismatch(2, 3));
        Assert.Equal(ClosedLoopStepStatus.Stopped, ClosedLoopRunner.AfterMismatch(3, 3)); // STOP → ask owner
        Assert.Equal(ClosedLoopStepStatus.Stopped, ClosedLoopRunner.AfterMismatch(4, 3)); // capped
    }
}

public sealed class GuiRiskClassificationTests
{
    [Fact]
    public void The_risk_tiers_match_the_plan()
    {
        // LOW: открыть/переключить; MEDIUM: отправить/создать; HIGH: удалить/деньги;
        // CRITICAL: credentials/security policy. The tiers feed the EXISTING AutonomyPolicy.
        Assert.True(Enum.GetValues<GuiActionRisk>().Length >= 4); // Low..Critical
        Assert.True((int)GuiActionRisk.Low < (int)GuiActionRisk.Medium);
        Assert.True((int)GuiActionRisk.Medium < (int)GuiActionRisk.High);
        Assert.True((int)GuiActionRisk.High < (int)GuiActionRisk.Critical);
    }
}

public sealed class SkillStoreTests
{
    private static ComputerUseSkill Skill(double confidence = 0.9, SkillStatus status = SkillStatus.Active, int version = 1) => new(
        Id: "send_message",
        Application: "messenger",
        AppVersion: "1.0",
        Version: version,
        Description: "Отправить сообщение контакту",
        Workflow:
        [
            new SkillWorkflowStep("click", "input:найти контакт", null, GuiActionRisk.Low, "state:contact search focused"),
            new SkillWorkflowStep("type", "input:search", "Мария", GuiActionRisk.Low, "state:contact found"),
            new SkillWorkflowStep("click", "list:contact-Maria", null, GuiActionRisk.Medium, "state:chat open"),
            new SkillWorkflowStep("type", "input:message", "привет", GuiActionRisk.Medium, "state:message typed"),
            new SkillWorkflowStep("click", "button:send", null, GuiActionRisk.Medium, "state:message sent")
        ],
        Provenance: SkillProvenance.Demonstration,
        Status: status,
        Confidence: confidence);

    [Fact]
    public void Skills_are_semantic_never_coordinates()
    {
        // треб. #19: «Find contact Maria → open», НЕ click(400,200) — every workflow
        // step targets a semantic role+id, never a raw coordinate.
        var skill = Skill();
        Assert.All(skill.Workflow, step => Assert.DoesNotMatch(@"click\(\d+", step.SemanticTarget));
        Assert.All(skill.Workflow, step => Assert.False(
            double.TryParse(step.SemanticTarget.Split(':').LastOrDefault(), out _)));
    }

    [Fact]
    public void Store_versions_strictly_monotonic()
    {
        var store = new SkillStore();
        Assert.True(store.Add(Skill(version: 1)).Ok);
        Assert.False(store.Add(Skill(version: 1)).Ok);                          // the same version — rejected
        Assert.True(store.Add(Skill(version: 2)).Ok);                            // higher — replaces
        Assert.Equal(2, store.TryGet("messenger", "send_message")!.Version);
    }

    [Fact]
    public void Confidence_lifecycle_low_confidence_no_risky_autonomous()
    {
        var store = new SkillStore();
        store.Add(Skill(confidence: 0.4)); // LOW_CONFIDENCE
        Assert.False(store.IsTrustedForAutonomous("messenger", "send_message"));
        // The successful reproduction raises the confidence.
        store.AdjustConfidence("messenger", "send_message", +0.3);
        Assert.True(store.IsTrustedForAutonomous("messenger", "send_message"));
        // A failure lowers it back below the trust.
        store.AdjustConfidence("messenger", "send_message", -0.5);
        Assert.False(store.IsTrustedForAutonomous("messenger", "send_message"));
    }

    [Fact]
    public void Disabled_skills_never_trusted()
    {
        var store = new SkillStore();
        store.Add(Skill(confidence: 0.9, status: SkillStatus.Disabled));
        Assert.False(store.IsTrustedForAutonomous("messenger", "send_message")); // Disabled = never used
    }

    [Fact]
    public void The_capacity_rejects_never_drops()
    {
        var store = new SkillStore();
        for (var i = 0; i < SkillStore.Capacity; i++)
            Assert.True(store.Add(Skill() with { Id = $"skill{i}", Application = $"app{i}" }).Ok);
        var (ok, error) = store.Add(Skill() with { Id = "overflow", Application = "overflow-app" });
        Assert.False(ok);                                       // the full store REJECTS — never silently drops
        Assert.NotNull(error);
    }

    [Fact]
    public void Invalid_skill_fails_closed()
    {
        var store = new SkillStore();
        Assert.False(store.Add(Skill() with { Workflow = [] }).Ok);            // no steps
        Assert.False(store.Add(Skill() with { Application = "" }).Ok);        // no application
    }
}
