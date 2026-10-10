using TOLIK.Desktop.Motion;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// The state → parameter mapping must be table-driven (no hardcoded branches),
/// fully cover the enum, keep states distinct, and fail closed for unknown states.
/// Phase 1 only ever activates Idle — but the machine must already be complete.
/// </summary>
public class StateMappingTests
{
    [Fact]
    public void Table_covers_every_state()
    {
        foreach (CephalonState state in Enum.GetValues<CephalonState>())
        {
            Assert.True(StateTraitTable.TryGet(state, out _), $"missing traits for {state}");
        }
        Assert.Equal(Enum.GetValues<CephalonState>().Length, StateTraitTable.Count);
    }

    [Fact]
    public void Every_state_has_distinct_traits()
    {
        var states = Enum.GetValues<CephalonState>().Cast<CephalonState>().ToList();
        var traits = states.Select(StateTraitTable.GetSafe).ToList();

        for (int i = 0; i < states.Count; i++)
        {
            for (int j = i + 1; j < states.Count; j++)
            {
                Assert.False(traits[i].Equals(traits[j]),
                    $"{states[i]} and {states[j]} map to identical traits");
            }
        }
    }

    [Fact]
    public void Second_state_drives_distinct_render_parameters()
    {
        // The extensibility contract: switching the animator to a second state
        // must move the body toward that state's mapped traits — a table lookup,
        // not an if/else in the animator.
        var idle = new CephalonAnimator(IdleMotionConfig.Default, new XorShiftRandom(0x11));
        RunFor(idle, seconds: 20);

        var thinking = new CephalonAnimator(IdleMotionConfig.Default, new XorShiftRandom(0x11));
        thinking.SetState(CephalonState.Thinking);
        RunFor(thinking, seconds: 20);

        StateTraits idleTraits = StateTraitTable.GetSafe(CephalonState.Idle);
        StateTraits thinkingTraits = StateTraitTable.GetSafe(CephalonState.Thinking);
        Assert.NotEqual(idleTraits, thinkingTraits);

        // Idle rests with an open eye; Thinking squints while it works.
        Assert.InRange(idle.Parameters.EyeOpen, 0.9, 1.0);
        Assert.InRange(thinking.Parameters.EyeOpen, 0.6, 0.8);
        Assert.True(thinking.Parameters.GlowIntensity > idle.Parameters.GlowIntensity,
            "Thinking glow must exceed idle glow");
        Assert.InRange(thinking.Parameters.PupilScale, 0.7, 0.96);
    }

    [Fact]
    public void Unmapped_state_fails_closed_to_calm_idle_traits()
    {
        StateTraits fallback = StateTraitTable.GetSafe((CephalonState)999);
        Assert.Equal(StateTraitTable.GetSafe(CephalonState.Idle), fallback);
        Assert.Equal(CephalonTone.Calm, fallback.Tone);
    }

    [Fact]
    public void Security_reserved_tones_are_never_calm()
    {
        // Red is reserved for real security/authorization states (Phase 2/3).
        // It must be reachable ONLY through those table rows — never by the default.
        Assert.Equal(CephalonTone.Error, StateTraitTable.GetSafe(CephalonState.Denied).Tone);
        Assert.Equal(CephalonTone.Locked, StateTraitTable.GetSafe(CephalonState.Locked).Tone);
        Assert.Equal(CephalonTone.Calm, StateTraitTable.GetSafe(CephalonState.Idle).Tone);
    }

    private static void RunFor(CephalonAnimator animator, double seconds)
    {
        const double dt = 1.0 / 60.0;
        int steps = (int)(seconds / dt);
        for (int i = 0; i < steps; i++)
        {
            animator.Update(dt);
        }
    }
}
