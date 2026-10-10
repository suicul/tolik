using TOLIK.Contracts;
using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Cephalon Expression Composer (docs/cephalon-desktop.md CE-2, директива §5/§18): the
/// deterministic mix — amusement + irritation + speaking produce ONE blended expression
/// (never three clips); continuous transitions (never instant); alarm priority set by
/// Core (Locked > Critical > voice); the real spectrum drives the body; garbage input
/// never renders as a spike; emotions never change permissions (the composer is pure).
/// Tests use EXPLICIT options — not env-dependent.
/// </summary>
public sealed class ExpressionComposerTests
{
    private static ComposerOptions Opts() => new() { FastRate = 10.0, MediumRate = 10.0, SlowRate = 10.0 };

    /// <summary>A nearly-converged compose (large dt + high rates ≈ the raw target mix).</summary>
    private static ExpressionOutput ComposeConverged(ExpressionState state) =>
        ExpressionComposer.Compose(state, ExpressionOutput.Neutral, 1.0, Opts());

    [Fact]
    public void Simultaneous_sources_produce_one_blended_expression()
    {
        // Директива §3: amusement=0.75, irritation=0.35, curiosity=0.20, attention=0.90,
        // speaking=true → ONE mixed expression: the spectrum + the playful pulse + the
        // asymmetry all present together — not three clips at once.
        var state = new ExpressionState(
            Attention: 0.9, Thinking: 0, Concentration: 0,
            Amusement: 0.75, Curiosity: 0.2, Frustration: 0.35, Confidence: 0, Uncertainty: 0,
            Engagement: 1.0,
            IsSpeaking: true, Amplitude: 0.6, LowBand: 0.5, MidBand: 0.4, HighBand: 0.7,
            SignalLevel: SystemSignalLevel.None,
            Processing: 0, TaskProgress: 0, Recovery: 0, Locked: false);
        var output = ComposeConverged(state);
        Assert.Equal(ExpressionMode.Speaking, output.Mode); // voice overrides the mode hint
        // The blend accumulates: ALL sources visible in one output.
        Assert.True(output.ShellDeformation > 0.4);  // low band → shell
        Assert.True(output.EyeScale > 1.05);         // mid band + curiosity → eye
        Assert.True(output.ParticleDensity > 0.4);   // high band → particles
        Assert.True(output.Asymmetry > 0.1);         // frustration → asymmetry (shape, not only color)
        Assert.True(output.PulseAmplitude > 0.3);    // low band + amusement → pulse
        Assert.True(output.SpectrumIntensity > 0.5); // amplitude → intensity
        Assert.True(output.AttentionIntensity > 0.8);
        Assert.True(output.IsFinite);
    }

    [Fact]
    public void Garbage_input_never_renders_a_spike()
    {
        // Директива §18: no NaN/incorrect values — normalization clamps to neutral.
        var garbage = new ExpressionState(
            Attention: double.NaN, Thinking: -5, Concentration: 99,
            Amusement: double.PositiveInfinity, Curiosity: 0.5, Frustration: -1, Confidence: 2, Uncertainty: 0.1,
            Engagement: 0.5,
            IsSpeaking: false, Amplitude: double.NaN, LowBand: -3, MidBand: 7, HighBand: 0.2,
            SignalLevel: (SystemSignalLevel)42,
            Processing: 0, TaskProgress: 0.5, Recovery: 0, Locked: false);
        var output = ComposeConverged(garbage);
        Assert.True(output.IsFinite); // no NaN/Infinity ever reaches the renderer
        Assert.InRange(output.EyeScale, 0.8, 1.3);
        Assert.InRange(output.EyeOpenness, 0.0, 1.0);
        Assert.InRange(output.GlowIntensity, 0.0, 1.0);
        Assert.InRange(output.Asymmetry, 0.0, 1.0);
    }

    [Fact]
    public void Transitions_are_never_instantaneous()
    {
        // Директива §5/§10: спокойствие → раздражение не меняет цвет и геометрию мгновенно.
        var calm = ExpressionState.Idle;
        var irritated = calm with { Frustration = 1.0 };
        var first = ExpressionComposer.Compose(irritated, ExpressionOutput.Neutral, 0.05,
            new ComposerOptions { FastRate = 4.0, MediumRate = 4.0, SlowRate = 4.0 });
        // After one short frame the output is still CLOSE to calm — approaching, not snapped.
        Assert.True(first.Asymmetry < 0.3);
        var second = ExpressionComposer.Compose(irritated, first, 0.05,
            new ComposerOptions { FastRate = 4.0, MediumRate = 4.0, SlowRate = 4.0 });
        // The second frame is FURTHER toward the target — a continuous curve.
        Assert.True(second.Asymmetry > first.Asymmetry);
        Assert.True(second.Asymmetry < 1.0);
    }

    [Fact]
    public void Transition_converges_over_time()
    {
        var target = ExpressionState.Idle with { Frustration = 1.0 };
        var output = ComposeConverged(target);
        Assert.True(output.Asymmetry > 0.4); // converged close to the 0.45 target
        Assert.True(output.MovementSpeed > 0.5);
    }

    [Fact]
    public void Locked_overrides_everything_fail_closed()
    {
        // Директива §10: приоритет тревоги устанавливает Core; locked — fail-closed visual.
        var state = new ExpressionState(
            Attention: 1, Thinking: 0, Concentration: 0,
            Amusement: 1, Curiosity: 1, Frustration: 1, Confidence: 1, Uncertainty: 0,
            Engagement: 1,
            IsSpeaking: true, Amplitude: 1, LowBand: 1, MidBand: 1, HighBand: 1,
            SignalLevel: SystemSignalLevel.Critical,
            Processing: 1, TaskProgress: 1, Recovery: 0, Locked: true);
        var output = ComposeConverged(state);
        Assert.Equal(ExpressionMode.Locked, output.Mode);
        Assert.True(output.EyeOpenness < 0.4);   // dimmed/closed
        Assert.True(output.MovementSpeed < 0.2); // still — not dancing while locked
        Assert.True(output.SpectrumIntensity < 0.5); // the alarm blend does not apply
    }

    [Fact]
    public void Critical_signal_beats_voice_mode()
    {
        var state = ExpressionState.Idle with
        {
            IsSpeaking = true, Amplitude = 0.5, LowBand = 0.5, MidBand = 0.5, HighBand = 0.5,
            SignalLevel = SystemSignalLevel.Critical
        };
        var output = ComposeConverged(state);
        Assert.Equal(ExpressionMode.Alert, output.Mode); // Core's priority wins the hint
        Assert.True(output.GlowIntensity > 0.5);         // the alarm blend is visible
    }

    [Fact]
    public void Warning_is_milder_than_error()
    {
        var warning = ComposeConverged(ExpressionState.Idle with { SignalLevel = SystemSignalLevel.Warning });
        var error = ComposeConverged(ExpressionState.Idle with { SignalLevel = SystemSignalLevel.Error });
        Assert.True(error.GlowIntensity > warning.GlowIntensity);
        Assert.True(error.PulseAmplitude > warning.PulseAmplitude);
    }

    [Fact]
    public void Spectrum_bands_map_to_their_layers()
    {
        // Директива §9: low → shell, mid → eye, high → particles, amplitude → intensity.
        var state = ExpressionState.Idle with
        {
            IsSpeaking = true, Amplitude = 0.8, LowBand = 0.9, MidBand = 0.1, HighBand = 0.2
        };
        var output = ComposeConverged(state);
        Assert.True(output.ShellDeformation > 0.6);       // low → shell (strong)
        Assert.True(output.ParticleDensity < 0.35);       // high → particles (weak here)
        var alt = ComposeConverged(state with { LowBand = 0.1, HighBand = 0.9 });
        Assert.True(alt.ParticleDensity > 0.6);           // high → particles (strong)
        Assert.True(alt.ShellDeformation < output.ShellDeformation);
        Assert.True(output.SpectrumIntensity > 0.7);      // amplitude → intensity
    }

    [Fact]
    public void Speech_interruption_transitions_smoothly()
    {
        // Директива §9: прерывание TTS пользователем — a smooth exit, not a snap.
        var speaking = ExpressionState.Idle with
        {
            IsSpeaking = true, Amplitude = 0.8, LowBand = 0.6, MidBand = 0.6, HighBand = 0.6
        };
        var midSpeech = ComposeConverged(speaking);
        var interrupted = ExpressionComposer.Compose(ExpressionState.Idle, midSpeech, 0.05, Opts());
        // The spectrum intensity DROPS toward 0 but is not instantly gone.
        Assert.True(interrupted.SpectrumIntensity < midSpeech.SpectrumIntensity);
        Assert.True(interrupted.SpectrumIntensity > 0.0);
    }

    [Fact]
    public void Disconnect_yields_honest_idle_no_fake_alarm()
    {
        // Директива §17: если Core недоступен — ограниченный режим, без ложного состояния.
        var output = ComposeConverged(ExpressionState.Idle);
        Assert.Equal(ExpressionMode.Idle, output.Mode);
        Assert.True(output.GlowIntensity is > 0.1 and < 0.5); // quiet presence
        Assert.True(output.IsFinite);
    }

    [Fact]
    public void Working_state_is_calm_and_precise()
    {
        // Директива §6: концентрация делает движения точнее и спокойнее.
        var processing = ExpressionState.Idle with { Processing = 1.0, Concentration = 1.0, TaskProgress = 0.5 };
        var output = ComposeConverged(processing);
        Assert.Equal(ExpressionMode.Working, output.Mode);
        Assert.True(output.MovementSpeed < 0.45);
        Assert.True(output.Asymmetry < 0.02); // concentration → no asymmetry
        Assert.True(output.PulseAmplitude > 0.05); // the subtle progress pulse
    }

    [Fact]
    public void Emotions_never_touch_authorization()
    {
        // Директива §18: the composer is a PURE function — (state, prev, dt) → output.
        // There is no permission/identity/trust surface in its signature at all.
        var output = ExpressionComposer.Compose(ExpressionState.Idle with { Frustration = 1.0 }, null, 1.0, Opts());
        Assert.True(output.IsFinite);
        Assert.Equal(ExpressionMode.Idle, output.Mode); // frustration ≠ alarm without Core's signal
        Assert.True(output.Asymmetry > 0.3); // expressed as SHAPE, never as authority
    }
}
