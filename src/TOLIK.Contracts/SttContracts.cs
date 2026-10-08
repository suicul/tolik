namespace TOLIK.Contracts;

/// <summary>Result of speech-to-text transcription.</summary>
public sealed record SttResult(
    string Text,
    string Language,
    double DurationSeconds,
    string Model)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Text);
}

/// <summary>Provider-agnostic speech-to-text abstraction for TOLIK Core.</summary>
public interface ISttProvider
{
    /// <summary>Transcribes audio bytes (wav/mp2/any format supported by the provider).</summary>
    Task<SttResult> TranscribeAsync(byte[] audio, CancellationToken cancellationToken = default);

    /// <summary>Health probe used by /status/modules.</summary>
    Task<VoiceHealth> CheckHealthAsync(CancellationToken cancellationToken = default);
}

/// <summary>Health snapshot for voice services (STT or TTS).</summary>
public sealed record VoiceHealth(bool Available, string Component, DateTimeOffset CheckedAtUtc)
{
    public string Status => Available ? "Healthy" : "Unavailable";
    public static VoiceHealth Unavailable(string component) => new(false, component, DateTimeOffset.UtcNow);
}

/// <summary>Options for the Whisper STT service binding (services/voice/server.py).</summary>
public sealed class WhisperOptions
{
    public const string SectionName = "TOLIK_STT";

    /// <summary>Base address of the Python voice service, e.g. http://127.0.0.1:9660.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Hard timeout for a single transcription request.</summary>
    public int TimeoutMs { get; set; } = 60_000;

    /// <summary>Maximum accepted audio payload size in bytes (default 20 MB).</summary>
    public int MaxAudioBytes { get; set; } = 20_971_520;
}

/// <summary>HTTP request for /voice/transcribe endpoint on TOLIK Core.</summary>
public sealed record TranscribeHttpRequest(byte[] Audio, string? FileName = null);

/// <summary>Full voice turn request: audio in, text + reply audio out.</summary>
public sealed record VoiceTurnRequest(
    byte[] Audio,
    VoiceMode Mode = VoiceMode.Normal,
    VoiceRuntimeMode RuntimeMode = VoiceRuntimeMode.Normal);

/// <summary>Full voice turn response: transcription, reply text and reply audio.</summary>
public sealed record VoiceTurnResponse(
    string Transcription,
    string ReplyText,
    VoiceMode Mode,
    byte[]? Audio,
    string? ContentType,
    string? Model,
    bool SpeakerVerified = false,
    double SpeakerConfidence = 0,
    string SpeakerTier = "none",
    string SpeakerReason = "not_evaluated");
