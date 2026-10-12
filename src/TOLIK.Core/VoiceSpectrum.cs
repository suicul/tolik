namespace TOLIK.Core;

/// <summary>The current voice levels for the expression (CE-3): all [0..1], the
/// honest zeros during the pauses.</summary>
public sealed record VoiceLevels(double Amplitude, double Low, double Mid, double High)
{
    public static VoiceLevels Silent { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// VoiceSpectrumState (CE-3 — PLAN REFORM PRG-2, директива §9): analyzes the REAL
/// TTS audio stream as it flows through Core — the amplitude + the three frequency
/// bands → the Cephalon expression (low → the shell, mid → the eye, high → the
/// particles; the total amplitude → the glow/movement). The analysis is streaming:
/// every outgoing PCM16 mono chunk feeds a bounded ring; the honest silence rule —
/// the reader sees zeros ~300 ms after the last sample (the pauses read as pauses,
/// never a fake waveform, §50). v1: ONE spectrum (TOLIK speaks one voice at a time;
/// per-turn spectra = a documented v2 refinement). Thread-safe; zero dependencies
/// (a hand-rolled iterative radix-2 FFT); no per-call allocations beyond the working
/// buffers; NaN never leaks.
/// </summary>
public sealed class VoiceSpectrumState
{
    public const int FftSize = 512;                       // ~32 ms @ 16 kHz
    public const int DefaultSampleRate = 16_000;
    private static readonly TimeSpan SilenceAfter = TimeSpan.FromMilliseconds(300);
    private const double BandEwmaAlpha = 0.35;            // the per-band smoothing (директива §9: подавляй дрожание)
    private const double AmplitudeGain = 3.0;             // the speech RMS is quiet; a documented deterministic gain

    private readonly object _gate = new();
    private readonly float[] _ring = new float[FftSize]; // the last FftSize samples [-1..1]
    private readonly double[] _fftReal = new double[FftSize];
    private readonly double[] _fftImag = new double[FftSize];
    private int _ringIndex;
    private DateTimeOffset _lastSampleAt = DateTimeOffset.MinValue;
    private double _amplitude, _low, _mid, _high;         // the smoothed [0..1] levels

    /// <summary>The current voice levels for the expression. The honest silence rule:
    /// zeros ~300 ms after the last audio (the getter itself never fabricates).</summary>
    public VoiceLevels Current(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_lastSampleAt == DateTimeOffset.MinValue || now - _lastSampleAt > SilenceAfter)
                return VoiceLevels.Silent;
            return new VoiceLevels(_amplitude, _low, _mid, _high);
        }
    }

    /// <summary>Feeds one outgoing TTS audio blob (WAV container: the RIFF/fmt/data
    /// parsing extracts the PCM; anything else — e.g. the edge-tts MP3 fallback — is
    /// SKIPPED honestly: the levels stay at zero rather than analyzing garbage).
    /// Cheap: O(FftSize log FftSize) per call.</summary>
    public void Analyze(ReadOnlySpan<byte> audioBlob, DateTimeOffset? at = null)
    {
        if (!TryExtractPcm16(audioBlob, out var pcm, out var sampleRate, out var channels))
            return; // not a 16-bit WAV (mono/stereo) — the honest skip, never garbage
        var now = at ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            // Append the chunk's samples to the ring ([-1..1] normalized; stereo → the mixdown).
            var step = channels; // 16-bit LE interleaved: the frame = `channels` samples
            var frames = pcm.Length / 2 / step;
            double chunkRms = 0;
            for (var i = 0; i < frames; i++)
            {
                double mixed = 0;
                for (var c = 0; c < channels; c++)
                {
                    var offset = (i * step + c) * 2;
                    var raw = (short)(pcm[offset] | (pcm[offset + 1] << 8));
                    mixed += raw / 32768.0;
                }
                var value = mixed / channels;
                chunkRms += value * value;
                _ring[_ringIndex] = (float)value;
                _ringIndex = (_ringIndex + 1) % FftSize;
            }
            if (frames == 0) return;
            chunkRms = Math.Sqrt(chunkRms / frames);

            // The FFT over the ring (the zero-padded portion reads as silence).
            for (var i = 0; i < FftSize; i++)
            {
                _fftReal[i] = _ring[i];
                _fftImag[i] = 0;
            }
            Fft(_fftReal, _fftImag);

            // The band energies: low < 500 Hz → the shell; 500-2500 → the eye; 2500-8000 → the particles.
            var binHz = (double)sampleRate / FftSize;
            var low = BandEnergy(_fftReal, _fftImag, 0, (int)(500 / binHz));
            var mid = BandEnergy(_fftReal, _fftImag, (int)(500 / binHz), (int)(2500 / binHz));
            var high = BandEnergy(_fftReal, _fftImag, (int)(2500 / binHz), (int)Math.Min(8000 / binHz, FftSize / 2));

            // The EWMA smoothing + the deterministic gain + the clamps (no NaN ever leaks).
            _amplitude = Smooth(_amplitude, Math.Clamp(chunkRms * AmplitudeGain, 0.0, 1.0));
            _low = Smooth(_low, low);
            _mid = Smooth(_mid, mid);
            _high = Smooth(_high, high);
            _lastSampleAt = now;
        }
    }

    /// <summary>A minimal WAV (RIFF) parser: 16-bit PCM, mono or stereo, any rate.
    /// Anything else (MP3, exotic formats) → false (the honest skip).</summary>
    private static bool TryExtractPcm16(ReadOnlySpan<byte> blob, out ReadOnlySpan<byte> pcm, out int sampleRate, out int channels)
    {
        pcm = default;
        sampleRate = DefaultSampleRate;
        channels = 1;
        if (blob.Length < 44) return false;
        if (blob[0] != (byte)'R' || blob[1] != (byte)'I' || blob[2] != (byte)'F' || blob[3] != (byte)'F') return false;
        if (blob[8] != (byte)'W' || blob[9] != (byte)'A' || blob[10] != (byte)'V' || blob[11] != (byte)'E') return false;
        // Walk the chunks: fmt (the format metadata) then data (the payload).
        var offset = 12;
        bool haveFormat = false;
        while (offset + 8 <= blob.Length)
        {
            var chunkId = blob[offset];
            var chunkSize = BitConverter.ToInt32(blob.Slice(offset + 4, 4));
            var body = offset + 8;
            if (chunkId == (byte)'f' && body + 16 <= blob.Length) // "fmt "
            {
                var format = BitConverter.ToInt16(blob.Slice(body, 2));
                channels = BitConverter.ToInt16(blob.Slice(body + 2, 2));
                sampleRate = BitConverter.ToInt32(blob.Slice(body + 4, 4));
                var bits = BitConverter.ToInt16(blob.Slice(body + 14, 2));
                if (format != 1 || (channels is not 1 and not 2) || bits != 16) return false;
                haveFormat = true;
            }
            else if (chunkId == (byte)'d' && haveFormat) // "data"
            {
                var length = Math.Min(chunkSize, blob.Length - body);
                pcm = blob.Slice(body, length);
                return pcm.Length >= 2;
            }
            offset = body + chunkSize + (chunkSize & 1); // the chunks are word-aligned
        }
        return false;
    }

    private static double Smooth(double current, double next) =>
        double.IsFinite(current) && double.IsFinite(next)
            ? Math.Clamp(current + (next - current) * BandEwmaAlpha, 0.0, 1.0)
            : 0.0;

    private static double BandEnergy(double[] real, double[] imag, int fromBin, int toBin)
    {
        if (toBin <= fromBin) return 0;
        var sum = 0.0;
        for (var i = fromBin; i < toBin; i++)
            sum += Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);
        // Normalize: the magnitudes sum for the worst-case full-scale sine ≈ FftSize/2.
        return Math.Clamp(sum / (FftSize * 0.25), 0.0, 1.0);
    }

    /// <summary>An iterative radix-2 Cooley-Tukey FFT in place (bit-reversal + butterflies).</summary>
    private static void Fft(double[] real, double[] imag)
    {
        var n = real.Length;
        // The bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }
        // The butterflies.
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2.0 * Math.PI / len;
            var wReal = Math.Cos(ang);
            var wImag = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double curReal = 1, curImag = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var evenReal = real[i + k];
                    var evenImag = imag[i + k];
                    var oddReal = real[i + k + len / 2] * curReal - imag[i + k + len / 2] * curImag;
                    var oddImag = real[i + k + len / 2] * curImag + imag[i + k + len / 2] * curReal;
                    real[i + k] = evenReal + oddReal;
                    imag[i + k] = evenImag + oddImag;
                    real[i + k + len / 2] = evenReal - oddReal;
                    imag[i + k + len / 2] = evenImag - oddImag;
                    (curReal, curImag) = (curReal * wReal - curImag * wImag, curReal * wImag + curImag * wReal);
                }
            }
        }
    }
}
