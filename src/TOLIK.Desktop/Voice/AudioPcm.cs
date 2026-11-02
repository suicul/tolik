namespace TOLIK.Desktop.Voice;

/// <summary>
/// PCM helpers for the microphone path. Pure functions, no devices involved, so the
/// conversion is testable — the capture itself is not.
///
/// The target format is what Core's STT expects: 16 kHz, mono, 16-bit little-endian,
/// wrapped in a WAV container. A USB interface often delivers 48 kHz stereo, and on a
/// two-input device a mono capture would hear only channel 1 — a microphone plugged
/// into input 2 would record as silence (the same trap the Agent hit).
/// </summary>
public static class AudioPcm
{
    public const int TargetSampleRate = 16000;
    public const int TargetChannels = 1;
    public const int TargetBitsPerSample = 16;

    /// <summary>
    /// Averages channels to mono and resamples to 16 kHz by nearest sample. Good
    /// enough for speech recognition: Whisper's own front end filters and windows the
    /// signal, so a heavier kernel here would add latency for no accuracy gain.
    /// </summary>
    public static byte[] MixDownTo16kMono(byte[] buffer, int bytesRecorded, int sourceRate, int sourceChannels)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (sourceRate <= 0 || sourceChannels <= 0)
        {
            return [];
        }

        int usable = Math.Min(bytesRecorded, buffer.Length);
        if (usable <= 0)
        {
            return [];
        }

        if (sourceRate == TargetSampleRate && sourceChannels == TargetChannels)
        {
            return buffer.AsSpan(0, usable).ToArray();
        }

        int frameBytes = 2 * sourceChannels;
        int frames = usable / frameBytes;
        if (frames == 0)
        {
            return [];
        }

        long outSamples = (long)frames * TargetSampleRate / sourceRate;
        if (outSamples == 0)
        {
            return [];
        }

        var result = new byte[outSamples * 2];
        double step = (double)sourceRate / TargetSampleRate;
        double position = 0.0;
        for (long i = 0; i < outSamples; i++)
        {
            int frame = (int)position;
            if (frame >= frames)
            {
                frame = frames - 1;
            }

            int offset = frame * frameBytes;
            int left = BitConverter.ToInt16(buffer, offset);
            int right = sourceChannels >= 2 ? BitConverter.ToInt16(buffer, offset + 2) : left;
            short mono = (short)((left + right) / 2);
            BitConverter.GetBytes(mono).CopyTo(result, i * 2);
            position += step;
        }
        return result;
    }

    /// <summary>Wraps raw 16-bit mono PCM in a minimal RIFF/WAVE header.</summary>
    public static byte[] ToWav(byte[] pcm, int sampleRate = TargetSampleRate, int channels = TargetChannels)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        const int headerSize = 44;
        var wav = new byte[headerSize + pcm.Length];
        int byteRate = sampleRate * channels * (TargetBitsPerSample / 8);
        short blockAlign = (short)(channels * (TargetBitsPerSample / 8));

        "RIFF"u8.CopyTo(wav.AsSpan(0));
        BitConverter.GetBytes(36 + pcm.Length).CopyTo(wav, 4);
        "WAVE"u8.CopyTo(wav.AsSpan(8));
        "fmt "u8.CopyTo(wav.AsSpan(12));
        BitConverter.GetBytes(16).CopyTo(wav, 16);            // fmt chunk size
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);      // PCM
        BitConverter.GetBytes((short)channels).CopyTo(wav, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
        BitConverter.GetBytes(byteRate).CopyTo(wav, 28);
        BitConverter.GetBytes(blockAlign).CopyTo(wav, 32);
        BitConverter.GetBytes((short)TargetBitsPerSample).CopyTo(wav, 34);
        "data"u8.CopyTo(wav.AsSpan(36));
        BitConverter.GetBytes(pcm.Length).CopyTo(wav, 40);
        pcm.CopyTo(wav, headerSize);
        return wav;
    }

    /// <summary>Peak amplitude (0..1) of 16-bit samples — drives the recording indicator.</summary>
    public static double PeakLevel(byte[] pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        int samples = pcm.Length / 2;
        if (samples == 0)
        {
            return 0.0;
        }

        int peak = 0;
        for (int i = 0; i < samples; i++)
        {
            int value = Math.Abs((int)BitConverter.ToInt16(pcm, i * 2));
            if (value > peak)
            {
                peak = value;
            }
        }
        return Math.Min(1.0, peak / 32768.0);
    }
}
