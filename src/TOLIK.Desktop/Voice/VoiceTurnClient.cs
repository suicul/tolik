using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using TOLIK.Desktop.Bridge;

namespace TOLIK.Desktop.Voice;

/// <summary>How a voice turn ended. One value, never an exception for the caller.</summary>
public sealed record VoiceTurnResult(VoiceTurnStatus Status, string? Detail, string Transcript)
{
    public bool IsSuccess => Status == VoiceTurnStatus.Ok || Status == VoiceTurnStatus.Silent;

    public static VoiceTurnResult Ok(string transcript) => new(VoiceTurnStatus.Ok, null, transcript);

    /// <summary>Core heard nothing (silence / pause) — a valid outcome, not an error.</summary>
    public static VoiceTurnResult Silent(string transcript) => new(VoiceTurnStatus.Silent, null, transcript);

    public static VoiceTurnResult Failed(VoiceTurnStatus status, string detail) => new(status, detail, string.Empty);
}

public enum VoiceTurnStatus
{
    Ok,
    /// <summary>Core answered "(пауза)" — nothing was heard.</summary>
    Silent,
    Unavailable,
    AuthRejected,
    Rejected,
    Malformed,
}

/// <summary>
/// Voice turns for the Windows client: records on the PC microphone, posts the audio
/// to Core, and streams back text + speech.
///
/// Where the work happens matters (AGENTS.md §14, §2): the client only captures and
/// plays. STT, speaker identification, authorization, memory and TTS all stay inside
/// Core and the Voice service — the microphone is an input device, not an authority.
///
/// The token travels through the same <see cref="ISessionTokenSource"/> the chat and
/// the expression link use. The client never logs or persists it.
/// </summary>
public sealed class VoiceTurnClient : IDisposable
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly CoreLinkOptions _options;
    private readonly ISessionTokenSource _tokens;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public VoiceTurnClient(CoreLinkOptions options, ISessionTokenSource tokens, HttpClient? http = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        if (http is null)
        {
            _http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) })
            {
                // A voice turn includes STT on CPU (medium: ~2.6 s), the model, and TTS.
                Timeout = TimeSpan.FromSeconds(180),
            };
            _ownsHttp = true;
        }
        else
        {
            _http = http;
        }
    }

    /// <summary>
    /// Sends one recorded turn. <paramref name="onEvent"/> receives text deltas and
    /// playable audio as they arrive, so playback can start before the turn ends.
    /// </summary>
    public async Task<VoiceTurnResult> SendAsync(
        byte[] audio,
        Action<VoiceStreamEvent>? onEvent,
        CancellationToken cancellationToken)
    {
        if (audio is null || audio.Length == 0)
        {
            return VoiceTurnResult.Failed(VoiceTurnStatus.Malformed, "нет записанного звука");
        }

        SessionToken? token = await _tokens.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return VoiceTurnResult.Failed(VoiceTurnStatus.Unavailable, _tokens.LastDetail ?? "нет сессии Core");
        }

        VoiceTurnResult result = await PostAsync(audio, token.Value.Value, onEvent, cancellationToken)
            .ConfigureAwait(false);

        if (result.Status != VoiceTurnStatus.AuthRejected || token.Value.Fresh)
        {
            return result;
        }

        _tokens.Invalidate();
        SessionToken? renewed = await _tokens.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (renewed is null)
        {
            return result;
        }
        return await PostAsync(audio, renewed.Value.Value, onEvent, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VoiceTurnResult> PostAsync(
        byte[] audio,
        string token,
        Action<VoiceStreamEvent>? onEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            string payload = JsonSerializer.Serialize(new
            {
                audio = Convert.ToBase64String(audio),
                mode = "Normal",
                runtimeMode = "Normal",
            }, PayloadOptions);

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.VoiceTurnStreamUri);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return VoiceTurnResult.Failed(VoiceTurnStatus.AuthRejected, $"сессия отклонена ({(int)response.StatusCode})");
            }
            if (!response.IsSuccessStatusCode)
            {
                return VoiceTurnResult.Failed(VoiceTurnStatus.Rejected, response.StatusCode switch
                {
                    HttpStatusCode.ServiceUnavailable => "распознавание или модель недоступны",
                    HttpStatusCode.BadRequest => "Core не принял звук",
                    _ => $"Core ответил {(int)response.StatusCode}",
                });
            }

            string contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                // The intent path answers with plain JSON (the documented behaviour of
                // this endpoint); it is not a stream and carries no audio.
                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseJsonAnswer(json);
            }

            return await ReadStreamAsync(response, onEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return VoiceTurnResult.Failed(VoiceTurnStatus.Unavailable, "Core недоступен");
        }
    }

    private static async Task<VoiceTurnResult> ReadStreamAsync(
        HttpResponseMessage response,
        Action<VoiceStreamEvent>? onEvent,
        CancellationToken cancellationToken)
    {
        var transcript = new StringBuilder();
        string? streamError = null;
        bool sawDone = false;

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // ReadLineAsync returns null at end of stream. Checking EndOfStream in an async
        // loop is a blocking peek (CA2024) and can stall on a long-lived SSE response.
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VoiceStreamEvent? parsed = VoiceStreamParser.ParseLine(line);
            if (parsed is null)
            {
                continue;
            }

            switch (parsed)
            {
                case VoiceStreamEvent.Delta delta:
                    transcript.Append(delta.Text);
                    break;
                case VoiceStreamEvent.Error error:
                    streamError ??= error.Reason;
                    break;
                case VoiceStreamEvent.Done:
                    sawDone = true;
                    break;
            }
            onEvent?.Invoke(parsed);
        }

        if (streamError is not null)
        {
            // An explicit Core error outranks "we saw no done": the turn did not finish.
            return VoiceTurnResult.Failed(
                streamError == "model_unavailable" ? VoiceTurnStatus.Rejected : VoiceTurnStatus.Malformed,
                streamError == "model_unavailable" ? "модель недоступна" : streamError);
        }
        if (!sawDone)
        {
            return VoiceTurnResult.Failed(VoiceTurnStatus.Malformed, "поток оборвался без завершения");
        }

        string text = transcript.ToString().Trim();
        if (text.Length == 0)
        {
            return VoiceTurnResult.Silent(string.Empty);
        }
        return VoiceTurnResult.Ok(text);
    }

    /// <summary>Non-stream answer (intent path): the same JSON shape as /voice/turn.</summary>
    private static VoiceTurnResult ParseJsonAnswer(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("answer", out JsonElement answer) || answer.ValueKind != JsonValueKind.String)
            {
                return VoiceTurnResult.Failed(VoiceTurnStatus.Malformed, "ответ Core без поля answer");
            }
            string text = answer.GetString() ?? string.Empty;
            string heard = root.TryGetProperty("transcription", out JsonElement t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? string.Empty
                : string.Empty;
            return text.StartsWith("(пауза)", StringComparison.Ordinal)
                ? VoiceTurnResult.Silent(heard)
                : VoiceTurnResult.Ok(text);
        }
        catch (JsonException)
        {
            return VoiceTurnResult.Failed(VoiceTurnStatus.Malformed, "неразборчивый ответ Core");
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
