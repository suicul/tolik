using TOLIK.Contracts;

namespace TOLIK.Core;

public sealed class DeterministicVoiceService : IVoiceService
{
    public async Task<VoiceResponse> ProcessAsync(VoiceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Transcription.IsValid) throw new ArgumentException("Invalid transcription.", nameof(request));
        if (request.Speaker is { IsValid: false }) throw new ArgumentException("Invalid speaker verification.", nameof(request));
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return new VoiceResponse(request.Transcription.Text.Trim(), request.Mode, request.Speaker?.Subject, request.Speaker?.Confidence);
    }
}
