namespace TOLIK.Contracts;

public enum VoiceMode { Normal, Tactical, Alert, Night, Combat }
public sealed record VoiceTranscription(string Text, double Confidence, string Provider, DateTimeOffset ObservedAt)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Text) && Confidence is >= 0 and <= 1 && !string.IsNullOrWhiteSpace(Provider);
}
public sealed record SpeakerVerification(string Subject, double Confidence, string Provider, DateTimeOffset ObservedAt)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Subject) && Confidence is >= 0 and <= 1 && !string.IsNullOrWhiteSpace(Provider);
}
public sealed record VoiceRequest(VoiceTranscription Transcription, SpeakerVerification? Speaker, VoiceMode Mode = VoiceMode.Normal);
public interface IVoiceService
{
    Task<VoiceResponse> ProcessAsync(VoiceRequest request, CancellationToken cancellationToken = default);
}
public sealed record VoiceResponse(string Text, VoiceMode Mode, string? SpeakerSubject, double? SpeakerConfidence);
