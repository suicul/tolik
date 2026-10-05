using TOLIK.Contracts;
using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Voice runtime modes (docs/voice-runtime-v2.md M8): Silent — TOLIK listens and
/// answers as text, never speaks; the game mute linkage — the detected game
/// auto-switches Normal ⇄ GamingAssist, manual modes are never overridden.
/// </summary>
public sealed class VoiceRuntimeModesTests
{
    [Fact]
    public void Silent_policy_disables_tts_but_keeps_stt()
    {
        var policy = VoiceRuntimePolicy.For(VoiceRuntimeMode.Silent);
        Assert.True(policy.SttEnabled);
        Assert.False(policy.TtsEnabled);
        Assert.Equal(TtsRoutingPreference.Fast, policy.TtsRouting);
    }

    [Theory]
    [InlineData("silent")]
    [InlineData("тихий")]
    [InlineData("тишина")]
    [InlineData("молчи")]
    public void TrySwitch_accepts_silent_aliases(string value)
    {
        var state = new VoiceRuntimeState();
        Assert.True(state.TrySwitch(value));
        Assert.Equal(VoiceRuntimeMode.Silent, state.Current);
    }

    [Fact]
    public void TrySwitch_rejects_unknown_mode()
    {
        var state = new VoiceRuntimeState();
        Assert.False(state.TrySwitch("bogus"));
        Assert.Equal(VoiceRuntimeMode.Normal, state.Current);
    }

    [Fact]
    public void Game_presence_auto_switches_normal_to_gaming_assist_and_back()
    {
        var state = new VoiceRuntimeState();
        Assert.True(state.ApplyGamePresence(true));
        Assert.Equal(VoiceRuntimeMode.GamingAssist, state.Current);
        Assert.True(state.ApplyGamePresence(false));
        Assert.Equal(VoiceRuntimeMode.Normal, state.Current);
    }

    [Fact]
    public void Game_presence_never_overrides_manual_modes()
    {
        var state = new VoiceRuntimeState();
        state.TrySwitch("night");
        Assert.False(state.ApplyGamePresence(true));
        Assert.Equal(VoiceRuntimeMode.Night, state.Current);

        state = new VoiceRuntimeState();
        state.TrySwitch("silent");
        Assert.False(state.ApplyGamePresence(true));
        Assert.Equal(VoiceRuntimeMode.Silent, state.Current);

        // A manually-set GamingAssist is not auto-restored either.
        state = new VoiceRuntimeState();
        state.TrySwitch("gamingassist");
        Assert.False(state.ApplyGamePresence(true));
        Assert.False(state.ApplyGamePresence(false));
        Assert.Equal(VoiceRuntimeMode.GamingAssist, state.Current);
    }

    [Fact]
    public void Manual_switch_cancels_auto_game_mode_linkage()
    {
        var state = new VoiceRuntimeState();
        Assert.True(state.ApplyGamePresence(true)); // auto -> GamingAssist
        Assert.True(state.TrySwitch("night")); // the owner's explicit choice
        Assert.False(state.ApplyGamePresence(false)); // no auto-restore over the manual mode
        Assert.Equal(VoiceRuntimeMode.Night, state.Current);
    }

    [Fact]
    public void Game_presence_is_idempotent_while_state_unchanged()
    {
        var state = new VoiceRuntimeState();
        Assert.True(state.ApplyGamePresence(true));   // Normal -> GamingAssist
        Assert.False(state.ApplyGamePresence(true));  // already in the game mode
        Assert.True(state.ApplyGamePresence(false));  // restore -> Normal
        Assert.False(state.ApplyGamePresence(false)); // already Normal
    }

    [Fact]
    public void Silent_intent_phrases_route_to_switch_voice_mode()
    {
        Assert.Equal(ChatIntentKind.SwitchVoiceMode, IntentRouter.Classify("тихий режим").Kind);
        Assert.Equal("Silent", IntentRouter.Classify("тихий режим").Argument);
        Assert.Equal(ChatIntentKind.SwitchVoiceMode, IntentRouter.Classify("молчи").Kind);
        Assert.Equal(ChatIntentKind.SwitchVoiceMode, IntentRouter.Classify("тишина").Kind);
    }

    [Fact]
    public void Single_word_mode_commands_never_become_implied_app_opens()
    {
        // Regression (latent bug): the implied "открой X" used to swallow these.
        Assert.Equal("Night", IntentRouter.Classify("ночь").Argument);
        Assert.Equal("Silent", IntentRouter.Classify("молчи").Argument);
        // The implied-open still works for genuine single-word app names.
        Assert.Equal(ChatIntentKind.OpenApp, IntentRouter.Classify("калькулятор").Kind);
    }

    private sealed class FakeTts : ITtsProvider
    {
        public TtsCapabilities Capabilities { get; } = new(TtsBackendKind.Fast, false, false, true, 100, "fake");
        public Task<TtsAudio> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new TtsAudio([1, 2, 3], "audio/mpeg", TimeSpan.Zero));
    }

    [Fact]
    public async Task Silent_gate_returns_no_audio_but_normal_mode_synthesizes()
    {
        var gate = new VoiceRuntimeGate(new FakeTts());
        var profile = new VoiceProfile("t", "ru-RU", 0, 0, 0, 0, 0, VoiceMode.Normal);
        Assert.Null(await gate.SynthesizeIfEnabledAsync(new TtsRequest("привет", profile, VoiceMode.Normal), VoiceRuntimeMode.Silent));
        Assert.NotNull(await gate.SynthesizeIfEnabledAsync(new TtsRequest("привет", profile, VoiceMode.Normal), VoiceRuntimeMode.Normal));
    }
}
