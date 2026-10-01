namespace TOLIK.Contracts;

public enum VoiceRuntimeMode { Normal, Gaming, GamingAssist, Night }
public sealed record VoiceProfile(
    string Name,
    string Language,
    int PitchSemitones,
    double FormantShift,
    double Compression,
    double Saturation,
    double VocoderMix,
    VoiceMode DefaultMode)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && Language == "ru-RU" && PitchSemitones is >= -24 and <= 24 && FormantShift is >= -1 and <= 1 && Compression is >= 0 and <= 1 && Saturation is >= 0 and <= 1 && VocoderMix is >= 0 and <= 1;
}
public sealed record VoiceRuntimePolicy(bool SttEnabled, bool TtsEnabled, bool LocalInferenceEnabled, bool HotkeyOnly)
{
    public static VoiceRuntimePolicy For(VoiceRuntimeMode mode) => mode switch
    {
        VoiceRuntimeMode.Gaming => new(false, false, false, false),
        VoiceRuntimeMode.GamingAssist => new(true, false, false, true),
        VoiceRuntimeMode.Night => new(true, true, false, false),
        _ => new(true, true, true, false)
    };
}
public sealed record TtsRequest(string Text, VoiceProfile Profile, VoiceMode Mode);
public sealed record TtsAudio(byte[] Data, string ContentType, TimeSpan Duration);
public interface ITtsProvider
{
    Task<TtsAudio> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken = default);
}
public sealed record CosyVoiceOptions(Uri Endpoint, TimeSpan Timeout, int MaxAudioBytes)
{
    public static CosyVoiceOptions FromEnvironment() => new(new Uri(Environment.GetEnvironmentVariable("TOLIK_COSYVOICE_ENDPOINT") ?? "http://127.0.0.1:9880/"), TimeSpan.FromSeconds(30), 10 * 1024 * 1024);
}
public sealed record CosyVoiceHealth(bool Available, string Status, DateTimeOffset CheckedAt);
public interface ILocalVoiceRuntime
{
    Task<CosyVoiceHealth> CheckHealthAsync(CancellationToken cancellationToken = default);
}
