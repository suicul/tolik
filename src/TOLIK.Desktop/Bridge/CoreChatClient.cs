using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TOLIK.Desktop.Bridge;

/// <summary>
/// Outcome of one chat turn. Deliberately a value, not an exception: the presence
/// window must render a failure honestly ("Core недоступен", "сессия отклонена")
/// instead of pretending the message was delivered.
/// </summary>
public sealed record ChatOutcome(ChatStatus Status, string? Answer, string? Detail)
{
    public static ChatOutcome Ok(string answer) => new(ChatStatus.Ok, answer, null);
    public static ChatOutcome Failed(ChatStatus status, string detail) => new(status, null, detail);

    public bool IsSuccess => Status == ChatStatus.Ok;
}

public enum ChatStatus
{
    Ok,
    /// <summary>Core not reachable (connection refused, DNS, timeout).</summary>
    Unavailable,
    /// <summary>401/403 — the session is not (or no longer) valid.</summary>
    AuthRejected,
    /// <summary>Core answered, but refused the request (4xx/5xx with a reason).</summary>
    Rejected,
    /// <summary>Malformed or unexpected body — never shown as an answer.</summary>
    Malformed,
}

/// <summary>
/// Text chat against TOLIK Core (<c>POST /chat</c>) for the Windows client.
///
/// Boundary rules (AGENTS.md §34, TOLIK__Harness §3.6): the client sends the
/// message and renders the answer — it does not authorize anything, does not
/// interpret tool results and never decides what is permitted. The token is
/// acquired through <see cref="ISessionTokenSource"/> and is never logged.
///
/// A 401/403 against a FRESH token is terminal (the session source says so), and
/// one retry is allowed against a cached token — the same honesty rule the
/// expression link already follows, so "no Core" and "auth rejected" never look
/// alike.
/// </summary>
public sealed class CoreChatClient : IDisposable, TOLIK.Desktop.Chat.IChatTransport
{
    /// <summary>
    /// Cyrillic must go on the wire as UTF-8, not as \\uXXXX escapes: the default
    /// encoder triples the payload for Russian and makes the body unreadable in a
    /// trace. Core parses both, but text is the normal case here, not the edge.
    /// </summary>
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly CoreLinkOptions _options;
    private readonly ISessionTokenSource _tokens;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public CoreChatClient(CoreLinkOptions options, ISessionTokenSource tokens, HttpClient? http = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        if (http is null)
        {
            _http = new HttpClient(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(3),
            })
            {
                // A reasoning model on a slow link can legitimately take a while;
                // the user sees a "думаю" state meanwhile.
                Timeout = TimeSpan.FromSeconds(120),
            };
            _ownsHttp = true;
        }
        else
        {
            _http = http;
        }
    }

    /// <summary>
    /// Sends one message and returns the answer. Never throws for transport or
    /// protocol failures — every failure is a described <see cref="ChatOutcome"/>.
    /// </summary>
    public async Task<ChatOutcome> SendAsync(string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return ChatOutcome.Failed(ChatStatus.Malformed, "пустое сообщение");
        }

        SessionToken? token = await _tokens.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return ChatOutcome.Failed(ChatStatus.Unavailable, _tokens.LastDetail ?? "нет сессии Core");
        }

        ChatOutcome outcome = await PostAsync(message, token.Value.Value, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != ChatStatus.AuthRejected || token.Value.Fresh)
        {
            return outcome;
        }

        // Cached token rejected → Core may have rotated sessions; one re-acquire.
        _tokens.Invalidate();
        SessionToken? renewed = await _tokens.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (renewed is null)
        {
            return outcome;
        }
        return await PostAsync(message, renewed.Value.Value, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ChatOutcome> PostAsync(string message, string token, CancellationToken cancellationToken)
    {
        try
        {
            string payload = JsonSerializer.Serialize(new { message }, PayloadOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.ChatUri);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _http
                .SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ChatOutcome.Failed(ChatStatus.AuthRejected, $"сессия отклонена ({(int)response.StatusCode})");
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return ChatOutcome.Failed(ChatStatus.Rejected, DescribeFailure(response.StatusCode, body));
            }

            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("answer", out JsonElement answer)
                || answer.ValueKind != JsonValueKind.String)
            {
                return ChatOutcome.Failed(ChatStatus.Malformed, "ответ Core без поля answer");
            }

            string text = answer.GetString() ?? string.Empty;
            return text.Length == 0
                ? ChatOutcome.Failed(ChatStatus.Malformed, "пустой ответ Core")
                : ChatOutcome.Ok(text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            return ChatOutcome.Failed(ChatStatus.Malformed, "неразборчивый ответ Core");
        }
        catch (Exception)
        {
            // Connection refused / DNS / timeout. The detail carries NO exception text:
            // transport messages can embed the endpoint, and this string reaches the UI.
            return ChatOutcome.Failed(ChatStatus.Unavailable, "Core недоступен");
        }
    }

    /// <summary>Token-free, private-data-free reason for a refusal (the body may echo the request).</summary>
    private static string DescribeFailure(HttpStatusCode status, string body)
    {
        string reason = status switch
        {
            HttpStatusCode.ServiceUnavailable => "модель недоступна",
            HttpStatusCode.BadRequest => "запрос отклонён Core",
            _ => $"Core ответил {(int)status}",
        };
        return string.IsNullOrWhiteSpace(body) ? reason : $"{reason}";
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
