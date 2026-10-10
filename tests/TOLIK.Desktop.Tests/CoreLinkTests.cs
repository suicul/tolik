using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using TOLIK.Desktop.Bridge;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// Endpoint configuration is parsed FAIL-CLOSED: an unreadable TOLIK_CORE_URL can
/// never become a live endpoint. The WS URI MUST URL-encode the token — dev tokens
/// are Base64 with '+' and '/', and a raw '+' in the query decodes as a space and
/// the handshake 401s (verified live against Core).
/// </summary>
public class CoreLinkOptionsTests
{
    [Fact]
    public void Empty_value_falls_back_to_the_loopback_default()
    {
        CoreLinkOptions options = CoreLinkOptions.FromEnvironment(raw: "");

        Assert.Equal(CoreLinkOptions.DefaultBase, options.BaseUri);
        Assert.Contains("default", options.SourceNote, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com")]        // wrong scheme — never a live endpoint
    [InlineData("file:///C:/DEV")]           // wrong scheme, looks local, still denied
    [InlineData("localhost:5080")]           // parses as a "localhost" SCHEME — rejected
    [InlineData("/relative/path")]
    public void Invalid_values_fail_closed_to_the_loopback_default(string raw)
    {
        CoreLinkOptions options = CoreLinkOptions.FromEnvironment(raw);

        Assert.Equal(CoreLinkOptions.DefaultBase, options.BaseUri);
        Assert.Contains(CoreLinkOptions.EnvVariable, options.SourceNote);
        Assert.Contains("invalid", options.SourceNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Valid_override_is_normalized_without_trailing_slash()
    {
        CoreLinkOptions options = CoreLinkOptions.FromEnvironment(raw: "http://192.168.1.5:8080/");

        Assert.Equal(new Uri("http://192.168.1.5:8080"), options.BaseUri);
        Assert.Equal(CoreLinkOptions.EnvVariable, options.SourceNote);
        Assert.Equal(new Uri("http://192.168.1.5:8080/auth/local-token"), options.LocalTokenUri);
    }

    [Fact]
    public void Environment_variable_is_read_and_cleared_honestly()
    {
        const string variable = "TOLIK_CORE_URL";
        try
        {
            Environment.SetEnvironmentVariable(variable, "http://10.1.2.3:9999");
            CoreLinkOptions fromEnv = CoreLinkOptions.FromEnvironment();
            Assert.Equal(new Uri("http://10.1.2.3:9999"), fromEnv.BaseUri);
            Assert.Equal(CoreLinkOptions.EnvVariable, fromEnv.SourceNote);

            Environment.SetEnvironmentVariable(variable, null);
            CoreLinkOptions unset = CoreLinkOptions.FromEnvironment();
            Assert.Equal(CoreLinkOptions.DefaultBase, unset.BaseUri);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void WebSocket_uri_encodes_the_base64_token_exactly_once()
    {
        CoreLinkOptions options = CoreLinkOptions.FromEnvironment(raw: "http://127.0.0.1:5080");
        // A Base64-shaped dev token: '+' and '/' MUST be escaped in the query.
        const string token = "AbC+/12xYz=";

        Uri uri = options.EventsWebSocketUri(token);

        Assert.Equal("ws", uri.Scheme);
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal(5080, uri.Port);
        Assert.Equal("/events/ws", uri.AbsolutePath);

        // The raw query carries the escapes; decoding it (what the server sees)
        // must return the token VERBATIM — the live '+'-decodes-as-space 401.
        Assert.Contains("%2B", uri.Query);
        Assert.Contains("%2F", uri.Query);
        Assert.Equal("token=AbC+/12xYz=", Uri.UnescapeDataString(uri.Query.TrimStart('?')));
    }

    [Fact]
    public void Https_base_upgrades_to_wss()
    {
        CoreLinkOptions options = CoreLinkOptions.FromEnvironment(raw: "https://core.example.com");

        Uri uri = options.EventsWebSocketUri("tok");

        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("core.example.com", uri.Host);
        Assert.Equal("/events/ws", uri.AbsolutePath);
        Assert.Equal("token=tok", Uri.UnescapeDataString(uri.Query.TrimStart('?')));
    }

    [Fact]
    public void WebSocket_uri_rejects_null_token()
    {
        CoreLinkOptions options = CoreLinkOptions.FromEnvironment();
        Assert.Throws<ArgumentNullException>(() => options.EventsWebSocketUri(null!));
    }
}

/// <summary>
/// Reconnect backoff must be exponential but CAPPED: the overlay always comes
/// back, and never hammers a struggling Core. The counter resets per successful
/// connection.
/// </summary>
public class ReconnectPolicyTests
{
    [Fact]
    public void Schedule_is_1_2_4_8_16_then_a_hard_30s_ceiling()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), ReconnectPolicy.DelayForAttempt(0));
        Assert.Equal(TimeSpan.FromSeconds(2), ReconnectPolicy.DelayForAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(4), ReconnectPolicy.DelayForAttempt(2));
        Assert.Equal(TimeSpan.FromSeconds(8), ReconnectPolicy.DelayForAttempt(3));
        Assert.Equal(TimeSpan.FromSeconds(16), ReconnectPolicy.DelayForAttempt(4));
        Assert.Equal(TimeSpan.FromSeconds(30), ReconnectPolicy.DelayForAttempt(5));
        Assert.Equal(TimeSpan.FromSeconds(30), ReconnectPolicy.DelayForAttempt(6));
        Assert.Equal(TimeSpan.FromSeconds(30), ReconnectPolicy.DelayForAttempt(1000));
    }

    [Fact]
    public void Negative_attempts_clamp_to_the_initial_delay()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), ReconnectPolicy.DelayForAttempt(-1));
        Assert.Equal(TimeSpan.FromSeconds(1), ReconnectPolicy.DelayForAttempt(-100));
    }

    [Fact]
    public void NextDelay_walks_the_schedule_and_Reset_starts_it_over()
    {
        var policy = new ReconnectPolicy();

        Assert.Equal(TimeSpan.FromSeconds(1), policy.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2), policy.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(4), policy.NextDelay());

        policy.Reset();
        Assert.Equal(TimeSpan.FromSeconds(1), policy.NextDelay());
    }
}

/// <summary>
/// Handshake failures must be classified: 401/403 is AUTH (terminal handling),
/// everything else is transport (retry). A missed 401 costs a polite retry; a
/// false "auth rejected" would stop the link forever — so unrecognizable
/// failures classify as transport.
/// </summary>
public class WebSocketFailureTests
{
    [Fact]
    public void Bcl_status_code_message_shape_is_auth()
    {
        // The real BCL message on a rejected WS handshake:
        var ex = new WebSocketException("The server returned status code '401': Unauthorized");

        Assert.Equal(CoreLinkFailure.AuthRejected, WebSocketFailure.Classify(ex));
    }

    [Fact]
    public void Http_request_exception_status_code_is_auth()
    {
        Assert.Equal(
            CoreLinkFailure.AuthRejected,
            WebSocketFailure.Classify(new HttpRequestException("denied", null, HttpStatusCode.Unauthorized)));
        Assert.Equal(
            CoreLinkFailure.AuthRejected,
            WebSocketFailure.Classify(new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden)));
    }

    [Fact]
    public void Deep_inner_exception_chains_are_searched()
    {
        var inner = new WebSocketException("The server returned status code '403': Forbidden");
        var middle = new HttpRequestException("handshake failed", inner);
        var outer = new Exception("outer", middle);

        Assert.Equal(CoreLinkFailure.AuthRejected, WebSocketFailure.Classify(outer));
    }

    [Fact]
    public void Plain_transport_trouble_is_transport()
    {
        Assert.Equal(CoreLinkFailure.Transport,
            WebSocketFailure.Classify(new WebSocketException("The remote party closed the connection")));
        Assert.Equal(CoreLinkFailure.Transport,
            WebSocketFailure.Classify(new HttpRequestException("connection refused", null)));
        Assert.Equal(CoreLinkFailure.Transport,
            WebSocketFailure.Classify(new OperationCanceledException()));
    }

    [Fact]
    public void Null_classifies_as_transport()
    {
        Assert.Equal(CoreLinkFailure.Transport, WebSocketFailure.Classify(null));
    }
}
