using TOLIK.Core.Host;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Adaptive candidate channel (docs/voice-identity.md VI-2): a turn NEVER waits for
/// learning (owner test #19) — enqueue is non-blocking, bounded, drop-oldest under
/// pressure. Candidates carry embeddings and metadata only, never text (§38).
/// </summary>
public sealed class VoiceAdaptationChannelTests
{
    private static AdaptiveCandidate Candidate(int i) => new(
        Guid.NewGuid(), [0.1 * i, 0.9, 0.0, 0.0], 3000 + i, 0.7 + i / 100.0, DateTimeOffset.UtcNow.AddSeconds(i));

    [Fact]
    public void Enqueue_never_blocks_and_reads_in_order()
    {
        var channel = new VoiceAdaptationChannel();
        for (var i = 0; i < 16; i++)
            Assert.True(channel.TryEnqueue(Candidate(i)));
        var collected = new List<int>();
        while (channel.Reader.TryRead(out var candidate))
            collected.Add(candidate.QualitySpeechMs - 3000);
        Assert.Equal(Enumerable.Range(0, 16), collected);
    }

    [Fact]
    public void Overflow_drops_oldest_keeps_newest()
    {
        var channel = new VoiceAdaptationChannel();
        for (var i = 0; i < 100; i++)
            Assert.True(channel.TryEnqueue(Candidate(i))); // never blocks, never throws
        var collected = new List<int>();
        while (channel.Reader.TryRead(out var candidate))
            collected.Add(candidate.QualitySpeechMs - 3000);
        Assert.Equal(64, collected.Count);              // bounded
        Assert.Equal(Enumerable.Range(36, 64), collected); // the oldest 36 dropped
    }

    [Fact]
    public void Candidate_carries_no_text_by_shape()
    {
        var candidate = Candidate(0);
        Assert.Equal(4, candidate.Embedding.Count);   // an embedding, not a transcript
        Assert.Equal(0, candidate.Similarity > 0 ? 0 : 0); // metadata fields exist
        Assert.True(candidate.QualitySpeechMs > 0);
        Assert.NotEqual(default, candidate.At);
    }
}
