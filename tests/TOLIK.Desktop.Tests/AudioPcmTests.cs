using TOLIK.Desktop.Voice;
using Xunit;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// Microphone audio preparation (CE-5 voice). These convert what a device delivers
/// into what Core's STT expects; a wrong rate or channel count would reach Whisper as
/// noise, and the owner would blame the assistant's hearing.
/// </summary>
public sealed class AudioPcmTests
{
    /// <summary>Builds interleaved 16-bit PCM from per-channel sample arrays.</summary>
    private static byte[] Build(params short[][] channels)
    {
        int frames = channels[0].Length;
        var bytes = new byte[frames * channels.Length * 2];
        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < channels.Length; c++)
            {
                BitConverter.GetBytes(channels[c][i]).CopyTo(bytes, (i * channels.Length + c) * 2);
            }
        }
        return bytes;
    }

    [Fact]
    public void Already_16k_mono_is_passed_through_untouched()
    {
        byte[] pcm = Build([100, -100, 200, -200]);

        byte[] result = AudioPcm.MixDownTo16kMono(pcm, pcm.Length, 16000, 1);

        Assert.Equal(pcm, result);
    }

    [Fact]
    public void Stereo_is_averaged_not_left_channel_only()
    {
        // Left +1000, right -1000 → mono 0; a channel-1-only bug would keep 1000.
        byte[] stereo = Build([1000, 1000], [-1000, -1000]);

        byte[] mono = AudioPcm.MixDownTo16kMono(stereo, stereo.Length, 16000, 2);

        Assert.Equal(4, mono.Length);
        Assert.Equal(0, BitConverter.ToInt16(mono, 0));
        Assert.Equal(0, BitConverter.ToInt16(mono, 2));
    }

    [Fact]
    public void Microphone_on_input_two_is_heard()
    {
        // Channel 1 silent, channel 2 carrying the voice: a mono capture would record silence.
        byte[] stereo = Build([0, 0, 0, 0], [3000, -3000, 3000, -3000]);

        byte[] mono = AudioPcm.MixDownTo16kMono(stereo, stereo.Length, 16000, 2);

        Assert.Equal(1500, BitConverter.ToInt16(mono, 0));
        Assert.Equal(-1500, BitConverter.ToInt16(mono, 2));
    }

    [Fact]
    public void Resampling_to_16k_shrinks_the_sample_count()
    {
        // 48 kHz → 16 kHz is a factor of three.
        var frames = new short[4800];
        byte[] pcm = Build(frames);

        byte[] mono = AudioPcm.MixDownTo16kMono(pcm, pcm.Length, 48000, 1);

        Assert.Equal(1600, mono.Length / 2);
    }

    [Fact]
    public void Empty_or_invalid_input_yields_empty_output()
    {
        Assert.Empty(AudioPcm.MixDownTo16kMono([], 0, 16000, 1));
        Assert.Empty(AudioPcm.MixDownTo16kMono(new byte[10], 10, 0, 1));
        Assert.Empty(AudioPcm.MixDownTo16kMono(new byte[10], 10, 16000, 0));
    }

    [Fact]
    public void Wav_header_is_a_real_riff_container()
    {
        byte[] pcm = Build([1000, -1000, 500, -500]);

        byte[] wav = AudioPcm.ToWav(pcm);

        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36 + pcm.Length, BitConverter.ToInt32(wav, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));            // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));            // mono
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));        // sample rate
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));           // bits
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(pcm.Length, BitConverter.ToInt32(wav, 40));
    }

    [Fact]
    public void Wav_payload_round_trips()
    {
        byte[] pcm = Build([7, -7, 9, -9]);

        byte[] wav = AudioPcm.ToWav(pcm);

        Assert.Equal(pcm, wav[44..]);
    }

    [Fact]
    public void Peak_level_reports_silence_and_full_scale()
    {
        Assert.Equal(0.0, AudioPcm.PeakLevel(Build([0, 0, 0])));
        // short.MaxValue is 32767, so full scale is 32767/32768 — the normalisation
        // divides by the negative extreme, not by the positive one.
        Assert.Equal(short.MaxValue / 32768.0, AudioPcm.PeakLevel(Build([short.MaxValue])), 6);
        // The negative extreme reaches exactly 1.0.
        Assert.Equal(1.0, AudioPcm.PeakLevel(Build([short.MinValue])));
        Assert.True(AudioPcm.PeakLevel(Build([16384, -16384])) > 0.4);
        Assert.Equal(0.0, AudioPcm.PeakLevel([]));
    }
}
