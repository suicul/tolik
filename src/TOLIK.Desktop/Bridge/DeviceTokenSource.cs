using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TOLIK.Desktop.Identity;

namespace TOLIK.Desktop.Bridge;

/// <summary>
/// The real client credential path: enroll once with the bootstrap code, then renew by
/// signing a one-time challenge with the device key.
///
/// Why this exists: the first desktop client could only obtain a session from
/// <c>GET /auth/local-token</c>, which Core refuses whenever <c>TOLIK_DEV_AUTO_TOKEN</c>
/// is off OR the Genesis ceremony has run. So the shipped client could not authenticate
/// against a real installation at all — it only appeared to work because the dev flag was
/// on during development. The dev endpoint stays exactly as it is (a deliberate
/// single-user convenience); this is the ordinary path agents and remote clients use.
///
/// The token is cached in MEMORY ONLY and never written to disk (SECURITY §6); the
/// device PRIVATE key never leaves the machine and is sealed with DPAPI.
/// </summary>
public sealed class DeviceTokenSource : ISessionTokenSource, IDisposable
{
    private readonly object _gate = new();
    private readonly CoreLinkOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly DeviceIdentity? _identity;
    private readonly Func<DeviceIdentity> _identityFactory;
    private readonly string? _bootstrapCode;
    private string? _cached;
    private string? _lastDetail;

    /// <summary>
    /// <paramref name="identity"/> null means the device is not registered yet; a
    /// <paramref name="bootstrapCode"/> is then required, because enrollment is the only
    /// way to obtain a key Core will accept.
    /// </summary>
    public DeviceTokenSource(
        CoreLinkOptions options,
        DeviceIdentity? identity,
        string? bootstrapCode = null,
        HttpClient? http = null,
        Func<DeviceIdentity>? identityFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _identity = identity;
        _bootstrapCode = bootstrapCode;
        _identityFactory = identityFactory ?? (() => DeviceIdentity.LoadOrCreate());
        if (http is null)
        {
            _http = new HttpClient(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(3),
            })
            {
                Timeout = TimeSpan.FromSeconds(10),
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

    /// <summary>True once a session has been obtained — lets the UI say "подключено".</summary>
    public bool HasSession
    {
        get { lock (_gate) { return _cached is not null; } }
    }

    /// <summary>The enrolled device identity, once one exists (null before enrollment).</summary>
    public DeviceIdentity? Identity => _identity;

    public async Task<SessionToken?> AcquireAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cached is not null)
            {
                return new SessionToken(_cached, Fresh: false);
            }
        }

        string? token = _identity is null
            ? await EnrollAsync(cancellationToken).ConfigureAwait(false)
            : await RenewAsync(cancellationToken).ConfigureAwait(false);

        if (token is null)
        {
            return null;
        }

        lock (_gate)
        {
            _cached = token;
            _lastDetail = null;
        }
        return new SessionToken(token, Fresh: true);
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    /// <summary>First-run enrollment. Consumes the bootstrap code, which Core allows once.</summary>
    private async Task<string?> EnrollAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_bootstrapCode))
        {
            SetDetail("устройство не зарегистрировано: нужен код регистрации");
            return null;
        }

        try
        {
            using var identity = _identityFactory();
            JsonDocument? challenge = await PostAsync(
                "/auth/bootstrap/challenge",
                new { bootstrapCode = _bootstrapCode },
                cancellationToken).ConfigureAwait(false);
            if (challenge is null)
            {
                return null;
            }

            using (challenge)
            {
                if (!TryReadString(challenge.RootElement, "challenge", out string? challengeToken))
                {
                    SetDetail("регистрация: Core не выдал задание");
                    return null;
                }

                string signature = identity.SignChallenge(challengeToken!);
                JsonDocument? enrolled = await PostAsync(
                    "/auth/bootstrap/enroll",
                    new
                    {
                        bootstrapCode = _bootstrapCode,
                        installationId = identity.InstallationId,
                        publicKey = identity.PublicKeyPem,
                        challenge = challengeToken,
                        signature,
                    },
                    cancellationToken).ConfigureAwait(false);
                if (enrolled is null)
                {
                    return null;
                }

                using (enrolled)
                {
                    if (!TryReadString(enrolled.RootElement, "sessionToken", out string? session))
                    {
                        SetDetail("регистрация отклонена: код недействителен или владелец уже зарегистрирован");
                        return null;
                    }
                    return session;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // DeviceIdentity refused to touch a damaged/foreign key file — say so plainly.
            SetDetail("ключ устройства недоступен (повреждён или чужой профиль Windows)");
            return null;
        }
        catch (HttpRequestException)
        {
            SetDetail("Core недоступен");
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetDetail("Core не ответил вовремя");
            return null;
        }
    }

    /// <summary>Ordinary path for every start after enrollment: challenge → sign → session.</summary>
    private async Task<string?> RenewAsync(CancellationToken cancellationToken)
    {
        DeviceIdentity identity = _identity!;
        try
        {
            JsonDocument? challenge = await PostAsync(
                "/auth/device/challenge",
                new { installationId = identity.InstallationId },
                cancellationToken,
                "устройство не зарегистрировано или отозвано — зарегистрируйте его заново").ConfigureAwait(false);
            if (challenge is null)
            {
                return null;
            }

            using (challenge)
            {
                if (!TryReadString(challenge.RootElement, "challenge", out string? challengeToken))
                {
                    SetDetail("продление сессии: Core не выдал задание");
                    return null;
                }

                string signature = identity.SignChallenge(challengeToken!);
                JsonDocument? issued = await PostAsync(
                    "/auth/device/session",
                    new
                    {
                        installationId = identity.InstallationId,
                        challenge = challengeToken,
                        signature,
                    },
                    cancellationToken).ConfigureAwait(false);
                if (issued is null)
                {
                    return null;
                }

                using (issued)
                {
                    if (!TryReadString(issued.RootElement, "sessionToken", out string? session))
                    {
                        SetDetail("продление сессии отклонено: устройство отозвано?");
                        return null;
                    }
                    return session;
                }
            }
        }
        catch (HttpRequestException)
        {
            SetDetail("Core недоступен");
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetDetail("Core не ответил вовремя");
            return null;
        }
    }

    /// <summary>POSTs JSON and parses the object. Every failure becomes an honest detail.
    /// <paramref name="deniedDetail"/> turns a 401 into a cause the user can act on,
    /// instead of a bare status code.</summary>
    private async Task<JsonDocument?> PostAsync(
        string path, object payload, CancellationToken cancellationToken, string? deniedDetail = null)
    {
        var uri = new Uri(_options.BaseUri, path);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            SetDetail(deniedDetail is not null && response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? deniedDetail
                : $"{path} → {(int)response.StatusCode}");
            return null;
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            SetDetail($"{path} → malformed response");
            return null;
        }
    }

    private static bool TryReadString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = element.GetString();
        return !string.IsNullOrEmpty(value);
    }

    private void SetDetail(string detail)
    {
        lock (_gate)
        {
            _lastDetail = detail;
        }
    }
}
