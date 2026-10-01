using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using TOLIK.Contracts;

namespace TOLIK.Core;

/// <summary>
/// HTTP client for the TOLIK Python voice service (services/voice/server.py).
/// The service is untrusted: all responses are treated as data, never as instructions.
/// </summary>
public sealed class WhisperSttProvider
{
    private readonly HttpClient _httpClient;
    private readonly WhisperOptions _options;

    public WhisperSttProvider(HttpClient httpClient, IOptions<WhisperOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        if (_options.Endpoint is not null)
            _httpClient.BaseAddress = _options.Endpoint;
        _httpClient.Timeout = TimeSpan.FromMilliseconds(_options.TimeoutMs > 0 ? _options.TimeoutMs : 60_000);
    }

    /// <summary>Transcribes raw audio bytes through the Python Whisper service.</summary>
    public async Task<SttResult> TranscribeAsync(byte[] audio, CancellationToken cancellationToken = default)
    {
        if (audio is null || audio.Length == 0) throw new ArgumentException("Audio payload is required.");
        if (audio.Length > _options.MaxAudioBytes) throw new ArgumentException("Audio payload exceeds the allowed size.");

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "audio.wav");

        using var response = await _httpClient.PostAsync("/stt/transcribe", form, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"whisper_transcribe_failed;status={(int)response.StatusCode};body={Truncate(body, 256)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        // Web defaults = case-insensitive binding: the service sends camelCase (durationSeconds).
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var result = await System.Text.Json.JsonSerializer.DeserializeAsync<SttResult>(stream, options, cancellationToken: cancellationToken);
        return result ?? throw new InvalidOperationException("whisper_transcribe_failed;empty_response");
    }

    /// <summary>Health probe — returns Availability without throwing.</summary>
    public async Task<VoiceHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (_options.Endpoint is null) return VoiceHealth.Unavailable("whisper");
        try
        {
            using var response = await _httpClient.GetAsync("/health", cancellationToken);
            if (!response.IsSuccessStatusCode) return VoiceHealth.Unavailable("whisper");
            return new VoiceHealth(true, "whisper", DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return VoiceHealth.Unavailable("whisper");
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
