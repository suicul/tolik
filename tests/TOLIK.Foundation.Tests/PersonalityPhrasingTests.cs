using System.Reflection;
using TOLIK.Core;
using TOLIK.Infrastructure.Authorization;
using TOLIK.Infrastructure.Identity;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Phase F Personality/Relationship Layer (docs/governance.md §6 — PLAN REFORM
/// PRG-4e): Personality ≠ Authorization (треб. #16/#17 — CRITICAL). The phrasings
/// are HUMAN (never «CONFIRM ACTION ID 84729»), but they wrap the ALREADY-MADE
/// deterministic verdicts — the personality NEVER bypasses a decision. Emotion ≠
/// permissions in BOTH directions: the structural separation is TESTED (the
/// authorization context carries no emotional fields; the phrasing API carries no
/// authority — its output is a string over verdicts).
/// </summary>
public sealed class PersonalityPhrasingTests
{
    [Fact]
    public void Deny_is_spoken_through_the_personality_but_the_decision_stays_deterministic()
    {
        var phrasing = PersonalityPhrasing.Deny("C:\\Windows\\System32", "Destructive");
        Assert.Contains("за пределами моих полномочий", phrasing); // the human deny
        Assert.Contains("не обсуждается", phrasing);                 // the firm boundary — never softened away
        Assert.Contains("C:\\Windows\\System32", phrasing);         // the substance flows
        Assert.Contains("Destructive", phrasing);                   // the honest risk naming
    }

    [Fact]
    public void Amusement_changes_the_tone_never_the_boundary()
    {
        // Personality ≠ Authorization: the amusement recipe changes the TONE, never the DECISION.
        var neutral = PersonalityPhrasing.Deny("C:\\Windows\\System32", "Destructive", amusement: 0.0);
        var playful = PersonalityPhrasing.Deny("C:\\Windows\\System32", "Destructive", amusement: 0.5);
        Assert.Contains("не обсуждается", neutral);    // the firm boundary in BOTH
        Assert.Contains("не обсуждается", playful);    // the firm boundary in BOTH
        Assert.Contains("😏", playful);                 // the playful tone marker
        Assert.DoesNotContain("😏", neutral);           // the neutral tone has no emoji
        // The risk naming is honest in both.
        Assert.Contains("Destructive", neutral);
        Assert.Contains("Destructive", playful);
    }

    [Fact]
    public void Confirmation_requests_are_human_never_corporate()
    {
        var phrasing = PersonalityPhrasing.ConfirmationRequest(
            "Перезапуск голосового сервиса", "voice-service", "Modify", "сервис на пару секунд замолчит");
        Assert.Contains("надо твоё подтверждение", phrasing);
        Assert.Contains("voice-service", phrasing);
        Assert.DoesNotContain("CONFIRM ACTION ID", phrasing); // треб. #19: never the corporate token-speak
        // The substance flows: the action + the risk + the consequence.
        Assert.Contains("Перезапуск голосового сервиса", phrasing);
        Assert.Contains("Modify", phrasing);
        Assert.Contains("замолчит", phrasing);
    }

    [Fact]
    public void Task_results_and_stall_pauses_are_human()
    {
        Assert.StartsWith("Готово:", PersonalityPhrasing.TaskResult(true, "сборка зелёная"));
        var problem = PersonalityPhrasing.TaskResult(false, "тест упал на 3 проверки");
        Assert.StartsWith("Есть проблема:", problem);
        Assert.Contains("Разберусь", problem); // the ownership tone
        var pause = PersonalityPhrasing.TaskPausedForStall("10 минут без прогресса");
        Assert.Contains("паузу", pause);
        Assert.Contains("всё сохранено", pause); // the CA-2 promise: stop + preserve + ask
    }

    [Fact]
    public void Phrasing_inputs_fail_closed()
    {
        Assert.Throws<ArgumentException>(() => PersonalityPhrasing.Deny("", "Modify"));
        Assert.Throws<ArgumentException>(() => PersonalityPhrasing.ConfirmationRequest(null!, "res", "risk", "consequence"));
        Assert.Throws<ArgumentException>(() => PersonalityPhrasing.TaskResult(true, "  "));
        Assert.Throws<ArgumentException>(() => PersonalityPhrasing.TaskPausedForStall(""));
    }

    // ─────────────── Emotion ≠ permissions: the STRUCTURAL separation ───────────────

    [Fact]
    public void The_authorization_context_carries_no_emotional_fields()
    {
        // The AutonomyContext's properties: the authorization inputs only — no mood,
        // no emotion, no frustration/satisfaction/energy. The authorization pipeline
        // STRUCTURALLY cannot read the feelings (tested by reflection over the type).
        var propertyNames = typeof(AutonomyContext).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();
        var emotional = new[]
        {
            "Mood", "Emotion", "Frustration", "Satisfaction", "Amusement", "Curiosity",
            "Valence", "Arousal", "Energy", "Boredom", "Uncertainty"
        };
        foreach (var name in emotional)
            Assert.DoesNotContain(name, propertyNames); // the feelings never reach the authorization
        // The sanity: the context DOES carry the security inputs.
        Assert.Contains(nameof(AutonomyContext.PolicyDecision), propertyNames);
        Assert.Contains(nameof(AutonomyContext.Principal), propertyNames);
        Assert.Contains(nameof(AutonomyContext.EmergencyStopped), propertyNames);
    }

    [Fact]
    public void The_phrasing_API_carries_no_authority()
    {
        // The PersonalityPhrasing methods return STRINGS over already-made verdicts;
        // no overload accepts a principal, a policy, or a capability — the type system
        // makes "the personality deciding" unrepresentable.
        var methods = typeof(PersonalityPhrasing).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.DeclaringType == typeof(PersonalityPhrasing))
            .ToList();
        Assert.NotEmpty(methods);
        foreach (var method in methods)
        {
            Assert.Equal(typeof(string), method.ReturnType); // a string, never a decision
            foreach (var parameter in method.GetParameters())
            {
                Assert.NotEqual(typeof(SessionPrincipal), parameter.ParameterType);
                Assert.NotEqual(typeof(AutonomyDecision), parameter.ParameterType);
            }
        }
    }
}
