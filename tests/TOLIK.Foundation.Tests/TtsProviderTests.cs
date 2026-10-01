using TOLIK.Contracts;
using TOLIK.Core;

namespace TOLIK.Foundation.Tests;

public sealed class TtsProviderTests
{
    private static VoiceProfile Profile => new("TOLIK Tactical Original", "ru-RU", -5, -.08, .72, .18, .28, VoiceMode.Tactical);

    [Fact]
    public async Task Provider_returns_audio_payload_and_duration()
    {
        var audio = await new DeterministicTtsProvider().SynthesizeAsync(new TtsRequest("Протокол активен.", Profile, VoiceMode.Tactical));
        Assert.NotEmpty(audio.Data); Assert.Equal("text/plain; charset=utf-8", audio.ContentType); Assert.True(audio.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task Gaming_gate_suppresses_tts()
    {
        var gate = new VoiceRuntimeGate(new DeterministicTtsProvider());
        Assert.Null(await gate.SynthesizeIfEnabledAsync(new TtsRequest("Не озвучивать", Profile, VoiceMode.Normal), VoiceRuntimeMode.Gaming));
    }
}
