using System.Net.Http;
using System.Text.Json;

namespace TOLIK.Desktop.Bridge;

/// <summary>A acquired-or-cached session credential. <see cref="Fresh"/> tells the
/// client whether this value was just minted (a 401 against a FRESH token is
/// terminal — retrying cannot help; a 401 against a cached one is worth one
/// re-acquisition, e.g. after Core rotated sessions).</summary>
public readonly record struct SessionToken(string Value, bool Fresh);

/// <summary>
/// Session credential source for the Core link. Implementations NEVER persist the
/// token (SECURITY §6: memory only), never log it, and never throw — failures return
/// null with an honest, token-free <see cref="LastDetail"/> for the debug overlay.
/// </summary>
public interface ISessionTokenSource
{
    /// <summary>Cached token, or a freshly acquired one. Null → unavailable right now.</summary>
    Task<SessionToken?> AcquireAsync(CancellationToken cancellationToken);

    /// <summary>Drops the cached credential (called on auth rejection — the next
    /// acquire mints a fresh session).</summary>
    void Invalidate();

    /// <summary>Honest, token-free description of the last acquisition failure.</summary>
    string? LastDetail { get; }
}

/// <summary>
/// Dev-machine token flow (the established pattern): GET {core}/auth/local-token →
/// {"sessionToken":"..."} — Core mints an owner session for loopback callers when
/// TOLIK_DEV_AUTO_TOKEN is on (src/TOLIK.Core.Host/Program.cs maps this endpoint as
/// GET; the browser UI uses GET too). A POST fallback covers deployments that only
/// accept POST — both verbs hit the same documented dev endpoint, no API is invented.
/// The token is cached in MEMORY ONLY and never written anywhere.
/// </summary>
public sealed class LocalTokenSource : ISessionTokenSource, IDisposable
{
    private readonly object _gate = new();
    private readonly CoreLinkOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private string? _cached;
    private string? _lastDetail;

    public LocalTokenSource(CoreLinkOptions options, HttpClient? http = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (http is null)
        {
            _http = new HttpClient(new SocketsHttpHandler
            {
                // Loopback-first dev flow: fail fast so the link keeps its rhythm.
                ConnectTimeout = TimeSpan.FromSeconds(3),
            })
            {
                Timeout = TimeSpan.FromSeconds(5),
            };
            _ownsHttp = true;
        }
        else
        {
            _http = http;
        }
    }

    public string? LastDetail
    {
        get { lock (_gate) { return _lastDetail; } }
    }

    public async Task<SessionToken?> AcquireAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cached is not null)
            {
                return new SessionToken(_cached, Fresh: false);
            }
        }

        string? acquired = await FetchAsync(usePost: false, cancellationToken).ConfigureAwait(false);
        if (acquired is null)
        {
            // GET can 404 (flag off / non-loopback / post-Genesis) or 405 — try POST once.
            acquired = await FetchAsync(usePost: true, cancellationToken).ConfigureAwait(false);
        }

        if (acquired is null)
        {
            return null;
        }

        lock (_gate)
        {
            _cached = acquired;
            _lastDetail = null;
        }
        return new SessionToken(acquired, Fresh: true);
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    /// <summary>Disposes the owned HttpClient (an injected one belongs to the caller).</summary>
    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private async Task<string?> FetchAsync(bool usePost, CancellationToken cancellationToken)
    {
        string verb = usePost ? "POST" : "GET";
        try
        {
            using var request = new HttpRequestMessage(
                usePost ? HttpMethod.Post : HttpMethod.Get, _options.LocalTokenUri);
            using HttpResponseMessage response = await _http
                .SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                SetDetail($"{verb} /auth/local-token → {(int)response.StatusCode} (dev auto-token unavailable)");
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("sessionToken", out JsonElement token)
                || token.ValueKind != JsonValueKind.String
                || token.GetString() is not { Length: > 0 } value)
            {
                SetDetail($"{verb} /auth/local-token → response without sessionToken");
                return null;
            }

            return value;
        }
        catch (JsonException)
        {
            SetDetail($"{verb} /auth/local-token → malformed response");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Connection refused, DNS, timeout — Core is simply not there. The detail
            // carries NO exception text: HttpRequestException messages may embed the
            // request URI, and the token lives in the WS query (defense in depth).
            SetDetail($"{verb} /auth/local-token → Core unreachable");
            return null;
        }
    }

    private void SetDetail(string detail)
    {
        lock (_gate)
        {
            _lastDetail = detail;
        }
    }
}
