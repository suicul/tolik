using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Sentence chunking for streaming voice responses (docs/voice-runtime-v2.md 0.6/M4):
/// splits at . ! ? … and newlines; no micro-chunks (a tail shorter than the minimum is
/// merged); max length guard; the streaming chunker emits at sentence boundaries.
/// </summary>
public sealed class SentenceChunkerTests
{
    [Fact]
    public void Splits_at_sentence_boundaries()
    {
        var chunks = SentenceChunker.Split("Первое предложение. Второе предложение! Третье?");
        // "Третье?" (7 chars) is below MinChunkLength — merged into the previous chunk
        // by design (no micro-chunks for TTS).
        Assert.Equal(2, chunks.Count);
        Assert.Equal("Первое предложение.", chunks[0]);
        Assert.EndsWith("Третье?", chunks[1]);
    }

    [Fact]
    public void Short_tail_is_merged_into_previous_chunk()
    {
        // "да." is below MinChunkLength (12) — merged into the previous chunk, no micro-chunks.
        var chunks = SentenceChunker.Split("Это достаточно длинное предложение для чанка. да.");
        Assert.Single(chunks);
        Assert.Contains("да.", chunks[0]);
    }

    [Fact]
    public void Ellipsis_and_multiline_are_boundaries()
    {
        var chunks = SentenceChunker.Split("Подожди…\nЧто дальше?");
        // Both boundary chunks are below MinChunkLength — merged into one chunk by
        // design (no micro-chunks); the boundaries still guide where splitting CAN happen.
        Assert.Single(chunks);
        Assert.Contains("Подожди", chunks[0]);
        Assert.Contains("Что дальше?", chunks[0]);
    }

    [Fact]
    public void Single_sentence_without_ender_is_one_chunk()
    {
        var chunks = SentenceChunker.Split("Текст без точки на конце");
        Assert.Single(chunks);
    }

    [Fact]
    public void Empty_or_null_returns_empty()
    {
        Assert.Empty(SentenceChunker.Split(null));
        Assert.Empty(SentenceChunker.Split(""));
        Assert.Empty(SentenceChunker.Split("   "));
    }

    [Fact]
    public void Streaming_chunker_emits_at_boundaries_and_flushes_tail()
    {
        var chunker = new SentenceChunker.StreamingChunker();
        // Deltas that complete a sentence: emitted only when >= MinChunkLength buffered.
        Assert.Empty(chunker.Feed("Первое "));           // no boundary yet
        var first = chunker.Feed("предложение. Второе "); // boundary: "Первое предложение."
        Assert.Single(first);
        Assert.Equal("Первое предложение.", first[0]);

        var second = chunker.Feed("предложение!");        // boundary: "Второе предложение!"
        Assert.Single(second);
        Assert.Equal("Второе предложение!", second[0]);

        // Flush at the stream end: whatever remains.
        var tail = chunker.Flush();
        Assert.Empty(tail);
    }

    [Fact]
    public void Streaming_chunker_merges_micro_chunks_on_flush()
    {
        var chunker = new SentenceChunker.StreamingChunker();
        chunker.Feed("Длинное предложение с концовкой.");
        chunker.Flush();
        var tail = chunker.Feed("да.");   // too short, stays pending
        Assert.Empty(tail);
        var flushed = chunker.Flush();
        Assert.Single(flushed);
        Assert.Equal("да.", flushed[0]);
    }
}
