using TOLIK.Contracts;
using TOLIK.Infrastructure.Authorization;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// The closed-loop verification step (CU-3 wiring): the ONLY syntax is
/// verify_gui:<target-spec>;expect=<substring> — the target is a well-formed
/// GuiTargetSpec, the expectation is a bounded literal substring (no wildcards, no
/// control chars). The wiring contract with ClosedLoopRunner.Verify: Verified = the
/// actual semantic state CONTAINS the expectation; Mismatch = everything else (the
/// step fails honestly — the plan composes the bounded retry, never an infinite
/// click-loop).
/// </summary>
public sealed class VerifyGuiStepTests
{
    [Theory]
    [InlineData("automation_id=btnSubmit;expect=Saved")]
    [InlineData("name=Сохранить;expect=Готово")]
    [InlineData("automation_id=btn; expect= after ")] // trimmed
    public void Well_formed_steps_parse(string step)
    {
        Assert.True(VerifyGuiStep.IsWellFormed(step));
        Assert.NotNull(VerifyGuiStep.TryParse(step));
    }

    [Fact]
    public void Target_and_expectation_are_exact()
    {
        var parsed = VerifyGuiStep.TryParse("automation_id=btnSubmit;expect=Saved");
        Assert.NotNull(parsed);
        Assert.Equal(GuiTargetKind.AutomationId, parsed.Target.Kind);
        Assert.Equal("btnSubmit", parsed.Target.Value);
        Assert.Equal("Saved", parsed.Expectation);
    }

    [Theory]
    [InlineData("automation_id=btnSubmit")] // no expectation part
    [InlineData("automation_id=btnSubmit;")] // empty part
    [InlineData(";expect=Saved")] // empty target
    [InlineData("btnSubmit;expect=Saved")] // malformed target (no kind=)
    [InlineData("coordinate=100,200;expect=Saved")] // coordinates are NEVER addressable
    [InlineData("automation_id=btn;saved=Saved")] // the part must be expect=
    [InlineData("automation_id=btn;expect=")] // empty expectation
    [InlineData("automation_id=btn;expect=   ")] // whitespace-only expectation
    [InlineData("automation_id=btn;expect=S*v*")] // wildcards — fail closed
    [InlineData("automation_id=btn;expect=Saved\nmore")] // control char — fail closed
    public void Malformed_steps_fail_closed(string step)
    {
        Assert.False(VerifyGuiStep.IsWellFormed(step));
        Assert.Null(VerifyGuiStep.TryParse(step));
    }

    [Fact]
    public void Oversized_expectation_fails_closed_at_256()
    {
        var oversized = new string('x', VerifyGuiStep.MaxExpectationLength + 1);
        Assert.False(VerifyGuiStep.IsWellFormed($"automation_id=btn;expect={oversized}"));
        var exact = new string('x', VerifyGuiStep.MaxExpectationLength);
        Assert.True(VerifyGuiStep.IsWellFormed($"automation_id=btn;expect={exact}"));
    }

    [Fact]
    public void Closed_loop_contract_verified_when_state_contains_expectation()
    {
        var parsed = VerifyGuiStep.TryParse("automation_id=btn;expect=after");
        Assert.NotNull(parsed);
        var action = new ComputerUseAction(
            "verify", parsed.Target.Value, TextInput: null, GuiActionRisk.Low, parsed.Expectation);

        // The follow-up read of the FLIPPED state contains the expectation → Verified
        // (the live demo's exact contract: the click flips "before" → "after").
        var status = ClosedLoopRunner.Verify(action, observedAfter: null,
            actualSemanticState: "controlType=ControlType.Text; name=after; isEnabled=true");
        Assert.Equal(ClosedLoopStepStatus.Verified, status);
    }

    [Fact]
    public void Closed_loop_contract_mismatch_when_state_lacks_expectation()
    {
        var parsed = VerifyGuiStep.TryParse("automation_id=btn;expect=after");
        Assert.NotNull(parsed);
        var action = new ComputerUseAction(
            "verify", parsed.Target.Value, TextInput: null, GuiActionRisk.Low, parsed.Expectation);

        // The state did NOT change (the click missed) → Mismatch → the step fails honestly.
        var status = ClosedLoopRunner.Verify(action, observedAfter: null,
            actualSemanticState: "controlType=ControlType.Text; name=before; isEnabled=true");
        Assert.Equal(ClosedLoopStepStatus.Mismatch, status);

        // An EMPTY state is also a Mismatch (never a silent pass).
        Assert.Equal(ClosedLoopStepStatus.Mismatch,
            ClosedLoopRunner.Verify(action, observedAfter: null, actualSemanticState: " "));

        // The bounded retry ladder: 3 attempts then STOP (never an infinite click-loop).
        Assert.Equal(ClosedLoopStepStatus.Pending, ClosedLoopRunner.AfterMismatch(1));
        Assert.Equal(ClosedLoopStepStatus.Pending, ClosedLoopRunner.AfterMismatch(2));
        Assert.Equal(ClosedLoopStepStatus.Stopped, ClosedLoopRunner.AfterMismatch(3));
    }
}
