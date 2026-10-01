using TOLIK.Contracts;

namespace TOLIK.Foundation.Tests;

public sealed class VoiceTests
{
    [Fact]
    public void Valid_transcription_and_speaker_are_accepted_independently()
    {
        var t = new VoiceTranscription("status", .95, "cloud-stt", DateTimeOffset.UtcNow);
        var s = new SpeakerVerification("Ivan", .42, "speaker-id", DateTimeOffset.UtcNow);
        Assert.True(t.IsValid);
        Assert.True(s.IsValid);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Confidence_out_of_range_is_rejected(double confidence)
    {
        var t = new VoiceTranscription("status", confidence, "cloud-stt", DateTimeOffset.UtcNow);
        Assert.False(t.IsValid);
    }
}
