namespace TOLIK.Desktop.Bridge;

/// <summary>
/// Core endpoint configuration, parsed FAIL-CLOSED (SECURITY §41): an unreadable,
/// relative or wrong-scheme <c>TOLIK_CORE_URL</c> never becomes a live endpoint —
/// the safe loopback default wins and the reason surfaces in the debug overlay.
/// Options carry endpoint data ONLY; the session token never lives here, never on
/// disk, and is never logged (SECURITY §6).
/// </summary>
/// <param name="BaseUri">HTTP(S) base address of TOLIK Core, no trailing slash.</param>
/// <param name="SourceNote">Honest provenance of the value (env / default / fallback).</param>
public sealed record CoreLinkOptions(Uri BaseUri, string SourceNote)
{
    public const string EnvVariable = "TOLIK_CORE_URL";
    public static readonly Uri DefaultBase = new("http://127.0.0.1:5080");

    /// <summary> Parses the endpoint from the environment (or an explicit override for
    /// tests). Invalid input never throws — it falls back to the loopback default.</summary>
    public static CoreLinkOptions FromEnvironment(string? raw = null)
    {
        raw ??= Environment.GetEnvironmentVariable(EnvVariable);
        string trimmed = (raw ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return new CoreLinkOptions(DefaultBase, "default (TOLIK_CORE_URL not set)");
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed)
            && (parsed.Scheme == "http" || parsed.Scheme == "https"))
        {
            // Normalize: no trailing slash, no default-port noise — one canonical form.
            string clean = parsed.GetComponents(UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped)
                .TrimEnd('/');
            return new CoreLinkOptions(new Uri(clean), "TOLIK_CORE_URL");
        }

        return new CoreLinkOptions(DefaultBase, $"invalid {EnvVariable} — loopback default used");
    }

    /// <summary>The dev token endpoint (same-machine minting, opt-in on Core).</summary>
    public Uri LocalTokenUri => new(BaseUri, "/auth/local-token");

    /// <summary>
    /// The realtime event WebSocket. The session token travels in the query string
    /// (the browser UI's connectWS pattern) and MUST be URL-encoded: dev tokens are
    /// Base64 containing '+' and '/', which a raw query parser would decode as spaces
    /// → guaranteed 401. The token is placed in the URI by the caller and must never
    /// be logged or displayed (SECURITY §6).
    /// </summary>
    public Uri EventsWebSocketUri(string sessionToken)
    {
        ArgumentNullException.ThrowIfNull(sessionToken);
        string scheme = BaseUri.Scheme == "https" ? "wss" : "ws";
        var builder = new UriBuilder(BaseUri)
        {
            Scheme = scheme,
            Path = "/events/ws",
            Query = "token=" + Uri.EscapeDataString(sessionToken),
        };
        return builder.Uri;
    }
}
