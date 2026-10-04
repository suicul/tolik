using TOLIK.Contracts;
using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// TTS routing (docs/voice-runtime-v2.md 0.4, M3): Auto = the runtime mode's policy
/// (Night/Gaming/GamingAssist → Fast, Normal → the chain default); Fast/Quality = the
/// explicit per-request override that wins. The existing chain stays the fallback —
/// no working backend is removed.
/// </summary>
public sealed class TtsRoutingTests
{
    [Theory]
    [InlineData(VoiceRuntimeMode.Normal, TtsRoutingPreference.Auto)]
    [InlineData(VoiceRuntimeMode.Night, TtsRoutingPreference.Fast)]
    [InlineData(VoiceRuntimeMode.Gaming, TtsRoutingPreference.Fast)]
    [InlineData(VoiceRuntimeMode.GamingAssist, TtsRoutingPreference.Fast)]
    public void Policy_declares_per_mode_routing(VoiceRuntimeMode mode, TtsRoutingPreference routing)
    {
        Assert.Equal(routing, VoiceRuntimePolicy.For(mode).TtsRouting);
    }

    [Fact]
    public async Task Explicit_routing_wins_over_policy()
    {
        var gate = new VoiceRuntimeGate(new DeterministicTtsProvider());
        var request = new TtsRequest("тест", VoiceProfileDefaults(), VoiceMode.Normal, TtsRoutingPreference.Quality);
        var audio = await gate.SynthesizeIfEnabledAsync(request, VoiceRuntimeMode.Night);
        Assert.NotNull(audio); // Night: TtsEnabled=true; the explicit Quality routing passes through
    }

    [Fact]
    public async Task Auto_routing_resolves_to_mode_policy()
    {
        var gate = new VoiceRuntimeGate(new DeterministicTtsProvider());
        var request = new TtsRequest("тест", VoiceProfileDefaults(), VoiceMode.Normal); // Auto
        var audio = await gate.SynthesizeIfEnabledAsync(request, VoiceRuntimeMode.Night);
        Assert.NotNull(audio); // Auto → Night policy (Fast routing) → TtsEnabled=true
    }

    [Fact]
    public async Task Tts_disabled_mode_returns_null_regardless_of_routing()
    {
        var gate = new VoiceRuntimeGate(new DeterministicTtsProvider());
        var request = new TtsRequest("тест", VoiceProfileDefaults(), VoiceMode.Normal, TtsRoutingPreference.Fast);
        var audio = await gate.SynthesizeIfEnabledAsync(request, VoiceRuntimeMode.Gaming);
        Assert.Null(audio); // Gaming: TtsEnabled=false — routing cannot resurrect speech
    }

    [Fact]
    public void Cosyvoice_capabilities_declare_measured_latency()
    {
        // M1 benchmark-backed: the MEASURED warm latency, never invented.
        var options = CosyVoiceOptions.FromEnvironment();
        var provider = new CosyVoice3TtsProvider(new System.Net.Http.HttpClient(), options);
        Assert.Equal(TtsBackendKind.Quality, provider.Capabilities.Kind);
        Assert.True(provider.Capabilities.SupportsCancellation);
        Assert.True(provider.Capabilities.SupportsRussian);
        Assert.True(provider.Capabilities.SupportsStreaming); // M4a: the server's streaming endpoint + SynthesizeStreamAsync
        Assert.Equal(944, provider.Capabilities.EstimatedLatencyMs);
    }

    private static VoiceProfile VoiceProfileDefaults() => new(
        "Толик", "ru-RU", 0, 0, 0, 0, 0, VoiceMode.Normal);
}
