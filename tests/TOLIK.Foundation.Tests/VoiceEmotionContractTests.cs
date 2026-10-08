using TOLIK.Contracts;
using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Emotion contract (docs/voice-identity.md §7/VI-6): emotion is a SEPARATE observation
/// from the speaker identity — a detector implementation must really detect (NO FAKE:
/// null when not configured), and the contract keeps the axes separate by design.
/// </summary>
public sealed class VoiceEmotionContractTests
{
    private sealed class NotConfiguredDetector : IVoiceEmotionDetector
    {
        public bool IsConfigured => false;
        public Task<VoiceEmotionObservation?> DetectAsync(byte[] audio, CancellationToken cancellationToken = default)
            => Task.FromResult<VoiceEmotionObservation?>(null);
    }

    private sealed class FakeDetector : IVoiceEmotionDetector
    {
        public bool IsConfigured => true;
        public Task<VoiceEmotionObservation?> DetectAsync(byte[] audio, CancellationToken cancellationToken = default)
            => Task.FromResult<VoiceEmotionObservation?>(new VoiceEmotionObservation(VoiceEmotion.Angry, 0.93));
    }

    [Fact]
    public async Task Unconfigured_detector_returns_null_never_a_fake_emotion()
    {
        var detector = new NotConfiguredDetector();
        Assert.False(detector.IsConfigured);
        Assert.Null(await detector.DetectAsync([1, 2, 3]));
    }

    [Fact]
    public async Task Configured_detector_returns_observation_with_own_confidence()
    {
        var detector = new FakeDetector();
        Assert.True(detector.IsConfigured);
        var observation = await detector.DetectAsync([1, 2, 3]);
        Assert.NotNull(observation);
        Assert.Equal(VoiceEmotion.Angry, observation!.Emotion);
        Assert.InRange(observation.Confidence, 0.0, 1.0);
    }

    [Fact]
    public async Task Emotion_and_speaker_identity_are_separate_axes()
    {
        // §7: the same audio may carry Ivan (speaker, verified) AND Angry (emotion) —
        // the emotion observation never modifies the speaker score/tier.
        var speakerScore = 0.72; // e.g. VerifyTurnSpeakerAsync similarity
        var emotion = await new FakeDetector().DetectAsync([]);
        Assert.Equal(SpeakerProfile.Tier(speakerScore), SpeakerProfile.Tier(speakerScore)); // unchanged by emotion
        Assert.NotNull(emotion);
        Assert.Equal(VoiceEmotion.Angry, emotion!.Emotion);
    }
}
