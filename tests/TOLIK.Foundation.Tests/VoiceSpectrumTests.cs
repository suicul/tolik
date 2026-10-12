using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// VoiceSpectrumState (CE-3 — PLAN REFORM PRG-2, директива §9): the REAL outgoing TTS
/// audio (the WAV path) analyzed into the amplitude + the three bands that drive the
/// Cephalon body (low → the shell, mid → the eye, high → the particles). The honest
/// silence rule (the zeros ~300 ms after the last audio — the pauses read as pauses);
/// the non-WAV blobs (the edge-tts MP3 fallback) skip honestly; the synthetic sines
/// land in their correct bands (the FFT correctness).
/// </summary>
public sealed class VoiceSpectrumTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static byte[] WavSine(double frequency, int sampleRate = 16_000, double seconds = 0.2, double amplitude = 0.6)
    {
        var samples = (int)(sampleRate * seconds);
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (short)(amplitude * 32000 * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
            pcm[i * 2] = (byte)(v & 0xFF);
            pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return WavWrap(pcm, sampleRate, channels: 1);
    }

    private static byte[] WavWrap(byte[] pcm, int sampleRate, int channels)
    {
        var wav = new byte[44 + pcm.Length];
        wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
        BitConverter.GetBytes(36 + pcm.Length).CopyTo(wav, 4);
        wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';
        wav[12] = (byte)'f'; wav[13] = (byte)'m'; wav[14] = (byte)'t'; wav[15] = (byte)' ';
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);                    // PCM
        BitConverter.GetBytes((short)channels).CopyTo(wav, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
        BitConverter.GetBytes(sampleRate * channels * 2).CopyTo(wav, 28);   // byte rate
        BitConverter.GetBytes((short)(channels * 2)).CopyTo(wav, 32);       // block align
        BitConverter.GetBytes((short)16).CopyTo(wav, 34);                    // bits
        wav[36] = (byte)'d'; wav[37] = (byte)'a'; wav[38] = (byte)'t'; wav[39] = (byte)'a';
        BitConverter.GetBytes(pcm.Length).CopyTo(wav, 40);
        pcm.CopyTo(wav, 44);
        return wav;
    }

    [Fact]
    public void Low_sine_lands_in_the_low_band()
    {
        var spectrum = new VoiceSpectrumState();
        var now = T0;
        for (var i = 0; i < 4; i++) { spectrum.Analyze(WavSine(200), now); now = now.AddMilliseconds(50); } // the EWMA converges
        var levels = spectrum.Current(now);
        Assert.True(levels.Low > levels.Mid, $"low {levels.Low} must beat mid {levels.Mid}");
        Assert.True(levels.Low > levels.High, $"low {levels.Low} must beat high {levels.High}");
        Assert.True(levels.Amplitude > 0.2); // a loud sine reads loud
    }

    [Fact]
    public void Mid_sine_lands_in_the_mid_band()
    {
        var spectrum = new VoiceSpectrumState();
        var now = T0;
        for (var i = 0; i < 4; i++) { spectrum.Analyze(WavSine(1000), now); now = now.AddMilliseconds(50); }
        var levels = spectrum.Current(now);
        Assert.True(levels.Mid > levels.Low, $"mid {levels.Mid} must beat low {levels.Low}");
        Assert.True(levels.Mid > levels.High, $"mid {levels.Mid} must beat high {levels.High}");
    }

    [Fact]
    public void High_sine_lands_in_the_high_band()
    {
        var spectrum = new VoiceSpectrumState();
        var now = T0;
        for (var i = 0; i < 4; i++) { spectrum.Analyze(WavSine(6000), now); now = now.AddMilliseconds(50); }
        var levels = spectrum.Current(now);
        Assert.True(levels.High > levels.Low, $"high {levels.High} must beat low {levels.Low}");
        Assert.True(levels.High > levels.Mid, $"high {levels.High} must beat mid {levels.Mid}");
    }

    [Fact]
    public void Silence_after_the_last_audio_reads_as_honest_zeros()
    {
        var spectrum = new VoiceSpectrumState();
        spectrum.Analyze(WavSine(1000), T0);
        Assert.True(spectrum.Current(T0.AddMilliseconds(50)).Amplitude > 0);   // playing
        Assert.Equal(VoiceLevels.Silent, spectrum.Current(T0.AddMilliseconds(400))); // the pause: zeros, never a fake waveform
    }

    [Fact]
    public void Non_wav_blobs_skip_honestly()
    {
        var spectrum = new VoiceSpectrumState();
        var mp3Like = new byte[] { 0xFF, 0xFB, 0x90, 0x00, 0x01, 0x02, 0x03, 0x04 }; // an MPEG frame sync — not a RIFF
        spectrum.Analyze(mp3Like, T0);
        Assert.Equal(VoiceLevels.Silent, spectrum.Current(T0.AddMilliseconds(50))); // no garbage analysis
        Assert.Equal(0, spectrum.Current(T0.AddMilliseconds(50)).Amplitude);
    }

    [Fact]
    public void Stereo_wav_mixes_down_and_analysis_works()
    {
        var spectrum = new VoiceSpectrumState();
        var samples = 3200; // 0.2 s mono worth of frames
        var pcm = new byte[samples * 4]; // stereo: 2 × int16 per frame
        for (var i = 0; i < samples; i++)
        {
            var v = (short)(0.6 * 32000 * Math.Sin(2 * Math.PI * 1000 * i / 16000));
            pcm[i * 4] = (byte)(v & 0xFF); pcm[i * 4 + 1] = (byte)((v >> 8) & 0xFF);
            pcm[i * 4 + 2] = (byte)(v & 0xFF); pcm[i * 4 + 3] = (byte)((v >> 8) & 0xFF);
        }
        var now = T0;
        for (var i = 0; i < 4; i++) { spectrum.Analyze(WavWrap(pcm, 16000, channels: 2), now); now = now.AddMilliseconds(50); }
        var levels = spectrum.Current(now);
        Assert.True(levels.Mid > levels.Low); // the mixdown preserves the band
        Assert.True(levels.Amplitude > 0.1);
    }

    [Fact]
    public void Voice_levels_flow_into_the_expression_state()
    {
        var voice = new VoiceLevels(Amplitude: 0.7, Low: 0.2, Mid: 0.6, High: 0.4);
        var state = TOLIK.Core.Host.CephalonExpressionService.ToExpressionState(
            new TOLIK.Core.Host.CephalonUserSnapshot(0, 0), locked: false, turnInFlight: true, connected: true,
            new MindEmotions(0, 0, 0, 0), voice);
        Assert.Equal(0.7, state.Amplitude, 6);
        Assert.Equal(0.2, state.LowBand, 6);
        Assert.Equal(0.6, state.MidBand, 6);
        Assert.Equal(0.4, state.HighBand, 6);
        // The composer maps them: low → the shell, mid → the eye, high → the particles (§9).
        var converged = TOLIK.Core.Host.CephalonExpressionService.ComposeFor(
            new TOLIK.Core.Host.CephalonUserSnapshot(0, 0), locked: false, turnInFlight: true, connected: true,
            new MindEmotions(0, 0, 0, 0), voice, previous: null, deltaSeconds: 1.0);
        Assert.True(converged.ShellDeformation > 0.3);  // the low band moves the shell
        Assert.True(converged.ParticleDensity > 0.3);    // the high band moves the particles
        Assert.True(converged.SpectrumIntensity > 0.5); // the amplitude drives the intensity
    }

    [Fact]
    public void Empty_and_tiny_blobs_are_safe()
    {
        var spectrum = new VoiceSpectrumState();
        spectrum.Analyze([], T0);
        spectrum.Analyze(new byte[3], T0);
        spectrum.Analyze(new byte[100], T0); // a RIFF-less short blob
        Assert.Equal(VoiceLevels.Silent, spectrum.Current(T0.AddMilliseconds(50)));
    }
}
