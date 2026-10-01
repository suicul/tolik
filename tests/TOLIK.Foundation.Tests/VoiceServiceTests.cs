using TOLIK.Contracts;
using TOLIK.Core;

namespace TOLIK.Foundation.Tests;

public sealed class VoiceServiceTests
{
    [Fact]
    public async Task Service_returns_transcription_and_independent_speaker_evidence()
    {
        var request = new VoiceRequest(new VoiceTranscription("  hello  ", .9, "test-stt", DateTimeOffset.UtcNow), new SpeakerVerification("Ivan", .2, "test-speaker", DateTimeOffset.UtcNow), VoiceMode.Tactical);
        var response = await new DeterministicVoiceService().ProcessAsync(request);
        Assert.Equal("hello", response.Text);
        Assert.Equal("Ivan", response.SpeakerSubject);
        Assert.Equal(.2, response.SpeakerConfidence);
    }

    [Fact]
    public async Task Cancellation_is_honored_before_response()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var request = new VoiceRequest(new VoiceTranscription("hello", .9, "test", DateTimeOffset.UtcNow), null);
        await Assert.ThrowsAsync<OperationCanceledException>(() => new DeterministicVoiceService().ProcessAsync(request, cts.Token));
    }
}
