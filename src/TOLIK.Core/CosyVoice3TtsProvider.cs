using System.Net.Http.Json;
using System.Text.Json;
using TOLIK.Contracts;

namespace TOLIK.Core;

public sealed class CosyVoice3TtsProvider(HttpClient client, CosyVoiceOptions options) : ITtsProvider, ILocalVoiceRuntime
{
    // M1 benchmark-backed declaration (docs/voice-runtime-v2.md): the MEASURED warm
    // latency (944ms), not invented. Streaming arrives in M4; cancellation exists.
    public TtsCapabilities Capabilities { get; } = new(
        TtsBackendKind.Quality,
        SupportsStreaming: false,
        SupportsCancellation: true,
        SupportsRussian: true,
        EstimatedLatencyMs: 944,
        QualityClass: "quality-gpu-xtts");

    public async Task<TtsAudio> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Invalid TTS request.", nameof(request));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        // services/voice/server.py: edge-tts (male Russian neural voice) or CosyVoice when configured.
        // Routing (0.4): the request's resolved preference passed through — fast=edge-only,
        // quality=XTTS-only, auto=the existing chain (XTTS -> edge -> espeak).
        using var response = await client.PostAsJsonAsync(
            new Uri(options.Endpoint, "tts/synthesize"),
            new { text = request.Text, speaker = "tolik", engine = request.Routing.ToString().ToLowerInvariant() },
            timeout.Token);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        if (bytes.Length == 0 || bytes.Length > options.MaxAudioBytes) throw new InvalidDataException("TTS service returned invalid audio.");
        return new TtsAudio(bytes, response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg", TimeSpan.Zero);
    }

    public async Task<CosyVoiceHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try { using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); cts.CancelAfter(options.Timeout); using var response = await client.GetAsync(new Uri(options.Endpoint, "health"), cts.Token); return new(response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "ready" : "unavailable", DateTimeOffset.UtcNow); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return new(false, "unavailable", DateTimeOffset.UtcNow); }
    }
}
