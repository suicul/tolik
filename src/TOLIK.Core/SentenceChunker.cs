using System.Text.RegularExpressions;

namespace TOLIK.Core;

/// <summary>
/// Sentence chunking for streaming voice responses (docs/voice-runtime-v2.md 0.6/M4):
/// splits streaming LLM output into speech-sized chunks at natural boundaries
/// (. ! ? … and newlines) with minimum/maximum length guards. Never yields
/// microscopic chunks (one token) — text shorter than the minimum is held and merged
/// with the next chunk. Deterministic and testable; the non-streaming path ignores it.
/// </summary>
public static partial class SentenceChunker
{
    // Sentence enders: . ! ? … (… as a single char U+2026 and the three-dot form) at
    // the end of a run of text, optionally followed by quotes/brackets.
    [GeneratedRegex(@"^[^\.!\?…]*[\.!\?…]+[""»\)\]]*\s*", RegexOptions.Compiled)]
    private static partial Regex SentenceEndRegex();

    public const int MinChunkLength = 12;
    public const int MaxChunkLength = 220;

    /// <summary>
    /// Splits a COMPLETE text into sentence chunks (the non-streaming convenience).
    /// A tail shorter than MinChunkLength is merged into the previous chunk.
    /// </summary>
    public static IReadOnlyList<string> Split(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var rest = text.Trim();
        while (rest.Length > 0)
        {
            var match = SentenceEndRegex().Match(rest);
            string chunk;
            if (match.Success && match.Length < rest.Length)
            {
                chunk = rest[..match.Length].Trim();
                rest = rest[match.Length..].Trim();
            }
            else
            {
                chunk = rest;
                rest = string.Empty;
            }
            if (chunk.Length == 0) continue;
            if (chunk.Length < MinChunkLength && result.Count > 0)
            {
                // Merge a too-short tail into the previous chunk (no micro-chunks).
                result[^1] = $"{result[^1]} {chunk}".Trim();
            }
            else
            {
                result.Add(chunk);
            }
        }
        return result;
    }

    /// <summary>
    /// Feeds streaming LLM deltas; emits chunks when a sentence boundary is found and
    /// the buffer holds at least MinChunkLength chars. Call Flush() at the stream end.
    /// </summary>
    public sealed class StreamingChunker
    {
        private readonly List<string> _pending = new();

        /// <summary>Feeds a delta; returns the completed chunks (may be empty, may be several).
        /// Emits when a boundary chunk reaches MinChunkLength; a too-short boundary chunk
        /// stays pending and merges with the following deltas (no micro-chunks).</summary>
        public IReadOnlyList<string> Feed(string? delta)
        {
            var emitted = new List<string>();
            if (!string.IsNullOrEmpty(delta)) _pending.Add(delta);
            while (true)
            {
                var bufferText = string.Concat(_pending).TrimStart();
                var match = SentenceEndRegex().Match(bufferText);
                if (!match.Success) break;
                var chunk = bufferText[..match.Length].Trim();
                if (chunk.Length < MinChunkLength) break;
                _pending.Clear();
                var tail = bufferText[match.Length..];
                if (tail.Length > 0) _pending.Add(tail);
                emitted.Add(chunk);
            }
            return emitted;
        }

        /// <summary>Flushes the remaining buffer at the stream end (the final chunk).</summary>
        public IReadOnlyList<string> Flush()
        {
            var rest = string.Concat(_pending).Trim();
            _pending.Clear();
            if (rest.Length == 0) return [];
            return [rest];
        }
    }
}
