using TOLIK.Contracts;
using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Direct mic path pieces (docs/voice-runtime-v2.md 0.9/0.10): utterance segmentation
/// from per-chunk VAD verdicts (pre-roll, end-silence, hard cap) and honest voice
/// activation resolution (Wake without a real detector falls back to Always — no fake).
/// </summary>
public sealed class UtteranceSegmenterTests
{
    // 16 kHz mono 16-bit = 32 bytes per millisecond.
    private static byte[] Chunk(int ms) => new byte[ms * 32];

    [Fact]
    public void Silence_chunks_return_null_and_never_open_capture()
    {
        var segmenter = new UtteranceSegmenter(preRollMs: 250, endSilenceMs: 700);
        Assert.Null(segmenter.Feed(Chunk(500), false));
        Assert.Null(segmenter.Feed(Chunk(500), false));
        Assert.Null(segmenter.Feed(Chunk(500), false));
        Assert.False(segmenter.IsCapturing);
        Assert.Null(segmenter.Flush());
    }

    [Fact]
    public void Pre_roll_precedes_first_speech_chunk()
    {
        var segmenter = new UtteranceSegmenter(preRollMs: 250, endSilenceMs: 700);
        // 600ms of silence -> only the last 250ms stay in the pre-roll queue.
        segmenter.Feed(Chunk(200), false);
        segmenter.Feed(Chunk(200), false);
        segmenter.Feed(Chunk(200), false);
        Assert.Null(segmenter.Feed(Chunk(100), true));
        Assert.True(segmenter.IsCapturing);
        var utterance = segmenter.Flush();
        Assert.NotNull(utterance);
        // 250ms pre-roll + 100ms speech = 350ms = 11200 bytes exactly.
        Assert.Equal(350 * 32, utterance!.Length);
    }

    [Fact]
    public void End_silence_closes_the_utterance()
    {
        var segmenter = new UtteranceSegmenter(preRollMs: 250, endSilenceMs: 700);
        Assert.Null(segmenter.Feed(Chunk(100), true));
        Assert.Null(segmenter.Feed(Chunk(400), false)); // 400ms < 700ms: still open
        Assert.True(segmenter.IsCapturing);
        var utterance = segmenter.Feed(Chunk(400), false); // trailing 800ms >= 700ms: close
        Assert.NotNull(utterance);
        Assert.Equal((100 + 400 + 400) * 32, utterance!.Length);
        Assert.False(segmenter.IsCapturing);
    }

    [Fact]
    public void Short_mid_utterance_silence_does_not_close()
    {
        var segmenter = new UtteranceSegmenter(preRollMs: 250, endSilenceMs: 700);
        segmenter.Feed(Chunk(100), true);
        Assert.Null(segmenter.Feed(Chunk(300), false)); // gap < threshold
        Assert.True(segmenter.IsCapturing);
        Assert.Null(segmenter.Feed(Chunk(100), true));  // speech resumes: trailing reset
        Assert.True(segmenter.IsCapturing);
        var utterance = segmenter.Flush();
        Assert.Equal((100 + 300 + 100) * 32, utterance!.Length);
    }

    [Fact]
    public void Hard_cap_forces_completion()
    {
        var segmenter = new UtteranceSegmenter(preRollMs: 250, endSilenceMs: 700, maxUtteranceMs: 1000);
        Assert.Null(segmenter.Feed(Chunk(600), true));
        Assert.True(segmenter.IsCapturing);
        var utterance = segmenter.Feed(Chunk(600), true); // 1200ms >= 1000ms cap
        Assert.NotNull(utterance);
        Assert.Equal(1200 * 32, utterance!.Length);
        Assert.False(segmenter.IsCapturing);
    }

    [Fact]
    public void Capture_reopens_fresh_after_completion()
    {
        var segmenter = new UtteranceSegmenter(preRollMs: 250, endSilenceMs: 700);
        segmenter.Feed(Chunk(100), true);
        Assert.NotNull(segmenter.Feed(Chunk(700), false)); // trailing 700ms >= 700ms: closes
        // Fresh state: silence buffers into the (empty) pre-roll, a new utterance opens.
        Assert.Null(segmenter.Feed(Chunk(100), false));
        Assert.False(segmenter.IsCapturing);
        Assert.Null(segmenter.Feed(Chunk(100), true));
        Assert.True(segmenter.IsCapturing);
        var second = segmenter.Flush();
        Assert.Equal((100 + 100) * 32, second!.Length);
    }

    [Fact]
    public void Constructor_validates_bounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UtteranceSegmenter(preRollMs: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UtteranceSegmenter(endSilenceMs: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UtteranceSegmenter(endSilenceMs: 700, maxUtteranceMs: 700));
    }
}

public sealed class VoiceActivationTests
{
    private sealed class NoDetector : IWakeWordDetector
    {
        public bool IsConfigured => false;
        public bool Process(ReadOnlySpan<byte> pcm16kMono) => false;
        public void Dispose() { }
    }

    [Theory]
    [InlineData("always", VoiceActivationMode.Always)]
    [InlineData("wake", VoiceActivationMode.Wake)]
    [InlineData("wake-word", VoiceActivationMode.Wake)]
    [InlineData("ptt", VoiceActivationMode.PushToTalk)]
    [InlineData("push-to-talk", VoiceActivationMode.PushToTalk)]
    [InlineData("", VoiceActivationMode.Always)]
    [InlineData("bogus", VoiceActivationMode.Always)]
    public void Parse_covers_all_modes(string value, VoiceActivationMode expected)
    {
        Assert.Equal(expected, VoiceActivation.Parse(value));
    }

    [Fact]
    public void Wake_without_detector_falls_back_honestly_to_always()
    {
        var (effective, reason) = VoiceActivation.Resolve(VoiceActivationMode.Wake, new NoDetector());
        Assert.Equal(VoiceActivationMode.Always, effective);
        Assert.NotNull(reason);
        Assert.Contains("no fake", reason);
    }

    [Fact]
    public void Always_resolves_without_fallback()
    {
        var (effective, reason) = VoiceActivation.Resolve(VoiceActivationMode.Always, detector: null);
        Assert.Equal(VoiceActivationMode.Always, effective);
        Assert.Null(reason);
    }

    [Fact]
    public void Push_to_talk_reports_the_hotkey_requirement()
    {
        var (effective, reason) = VoiceActivation.Resolve(VoiceActivationMode.PushToTalk, detector: null);
        Assert.Equal(VoiceActivationMode.PushToTalk, effective);
        Assert.NotNull(reason);
    }
}
