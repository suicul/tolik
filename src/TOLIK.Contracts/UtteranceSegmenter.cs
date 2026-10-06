namespace TOLIK.Contracts;

/// <summary>
/// Utterance segmentation (voice-runtime-v2.md 0.10): turns per-chunk VAD verdicts
/// (M6 /vad/detect) into complete utterances. Pre-roll keeps the silence just before
/// speech onset so the first word is not clipped; end-silence closes the utterance;
/// a hard cap bounds runaway capture. Client-side capture logic (agent/desktop) —
/// Core stays the authority: this only shapes LOCAL audio into a turn request.
/// Chunk sizes may vary; every byte is accounted, timing derives from the byte count.
/// </summary>
public sealed class UtteranceSegmenter
{
    private readonly int _bytesPerMs;
    private readonly int _preRollBytes;
    private readonly int _endSilenceBytes;
    private readonly int _maxUtteranceBytes;
    private readonly Queue<byte[]> _tail = new(); // recent silence before onset (pre-roll)
    private readonly List<byte[]> _utterance = [];
    private int _tailBytes;
    private int _utteranceBytes;
    private int _trailingSilenceBytes;
    private bool _capturing;

    public bool IsCapturing => _capturing;

    public UtteranceSegmenter(
        int preRollMs = 250,
        int endSilenceMs = 700,
        int maxUtteranceMs = 10_000,
        int sampleRate = 16_000,
        int bytesPerSample = 2)
    {
        if (preRollMs < 0) throw new ArgumentOutOfRangeException(nameof(preRollMs));
        if (endSilenceMs <= 0) throw new ArgumentOutOfRangeException(nameof(endSilenceMs));
        if (maxUtteranceMs <= endSilenceMs) throw new ArgumentOutOfRangeException(nameof(maxUtteranceMs));
        _bytesPerMs = sampleRate * bytesPerSample / 1000;
        _preRollBytes = preRollMs * _bytesPerMs;
        _endSilenceBytes = endSilenceMs * _bytesPerMs;
        _maxUtteranceBytes = maxUtteranceMs * _bytesPerMs;
    }

    /// <summary>
    /// Feeds one mic chunk with its VAD verdict. Returns the complete utterance
    /// (raw 16 kHz mono PCM, pre-roll included) when the end-silence or the hard cap
    /// closes it; null while the utterance is still open or nothing is capturing.
    /// </summary>
    public byte[]? Feed(byte[] chunk, bool hasSpeech)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Length == 0) return null;

        if (!_capturing)
        {
            if (!hasSpeech)
            {
                // Idle: keep only the last pre-roll worth of silence.
                _tail.Enqueue(chunk);
                _tailBytes += chunk.Length;
                while (_tailBytes > _preRollBytes)
                {
                    var overflow = _tailBytes - _preRollBytes;
                    var front = _tail.Peek();
                    if (front.Length <= overflow)
                    {
                        _tailBytes -= front.Length;
                        _tail.Dequeue();
                    }
                    else
                    {
                        var keep = front.Length - overflow;
                        var trimmed = new byte[keep];
                        Array.Copy(front, overflow, trimmed, 0, keep);
                        _tailBytes -= overflow;
                        _tail.Dequeue();
                        _tail.Enqueue(trimmed);
                        break;
                    }
                }
                return null;
            }
            // Silence -> speech: the utterance opens with the pre-roll first.
            StartCapture();
        }

        _utterance.Add(chunk);
        _utteranceBytes += chunk.Length;
        if (hasSpeech)
        {
            _trailingSilenceBytes = 0;
        }
        else
        {
            _trailingSilenceBytes += chunk.Length;
            if (_trailingSilenceBytes >= _endSilenceBytes)
                return Complete();
        }
        if (_utteranceBytes >= _maxUtteranceBytes)
            return Complete();
        return null;
    }

    /// <summary>Closes the open utterance (capture stopped / mode switch). Null when idle.</summary>
    public byte[]? Flush() => _capturing ? Complete() : null;

    private void StartCapture()
    {
        _capturing = true;
        _utterance.Clear();
        _utteranceBytes = 0;
        _trailingSilenceBytes = 0;
        foreach (var pre in _tail)
        {
            _utterance.Add(pre);
            _utteranceBytes += pre.Length;
        }
        _tail.Clear();
        _tailBytes = 0;
    }

    private byte[] Complete()
    {
        var result = new byte[_utteranceBytes];
        var offset = 0;
        foreach (var part in _utterance)
        {
            Array.Copy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        _capturing = false;
        _utterance.Clear();
        _utteranceBytes = 0;
        _trailingSilenceBytes = 0;
        return result;
    }
}

