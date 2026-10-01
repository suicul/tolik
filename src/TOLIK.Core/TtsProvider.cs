using TOLIK.Contracts;

namespace TOLIK.Core;

public sealed class DeterministicTtsProvider : ITtsProvider
{
    public async Task<TtsAudio> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Profile.IsValid || string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Invalid TTS request.", nameof(request));
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        var data = System.Text.Encoding.UTF8.GetBytes(request.Text);
        return new TtsAudio(data, "text/plain; charset=utf-8", TimeSpan.FromMilliseconds(Math.Max(250, request.Text.Length * 45)));
    }
}

public sealed class VoiceRuntimeGate(ITtsProvider ttsProvider)
{
    public async Task<TtsAudio?> SynthesizeIfEnabledAsync(TtsRequest request, VoiceRuntimeMode runtimeMode, CancellationToken cancellationToken = default)
    {
        var policy = VoiceRuntimePolicy.For(runtimeMode);
        return policy.TtsEnabled ? await ttsProvider.SynthesizeAsync(request, cancellationToken) : null;
    }
}

public sealed class LocalVoiceRuntimeGate(ITtsProvider ttsProvider)
{
    public Task<TtsAudio?> SynthesizeIfEnabledAsync(TtsRequest request, VoiceRuntimeMode mode, CancellationToken cancellationToken = default)
        => VoiceRuntimePolicy.For(mode).LocalInferenceEnabled && VoiceRuntimePolicy.For(mode).TtsEnabled
            ? ttsProvider.SynthesizeAsync(request, cancellationToken).ContinueWith(t => (TtsAudio?)t.Result, cancellationToken)
            : Task.FromResult<TtsAudio?>(null);
}
