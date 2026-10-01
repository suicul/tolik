using System.Net.Http.Json;
using System.Text.Json;
using TOLIK.Contracts;

namespace TOLIK.Core;

public sealed class CosyVoice3TtsProvider(HttpClient client, CosyVoiceOptions options) : ITtsProvider, ILocalVoiceRuntime
{
    public async Task<TtsAudio> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Invalid TTS request.", nameof(request));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        // services/voice/server.py: edge-tts (male Russian neural voice) or CosyVoice when configured.
        using var response = await client.PostAsJsonAsync(
            new Uri(options.Endpoint, "tts/synthesize"),
            new { text = request.Text, speaker = "tolik" },
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
