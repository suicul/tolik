using System.Net;
using System.Net.Http;
using System.Text;
using TOLIK.Desktop.Bridge;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// Chat transport contract (CE-5). The Windows client is the main client, so its
/// chat path must fail honestly and never invent an answer: these tests pin the
/// mapping between Core's real responses and what the presence window renders.
/// No network: a stub handler stands in for Core.
/// </summary>
public sealed class CoreChatClientTests
{
    private static readonly CoreLinkOptions Options = new(new Uri("http://127.0.0.1:5080"), "test");

    /// <summary>Stub Core: records the request and replies with a scripted response.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<(string Uri, string? Body, string? Auth)> Seen { get; } = [];

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Seen.Add((request.RequestUri!.ToString(), LastBody, request.Headers.Authorization?.Parameter));
            return _respond(request);
        }
    }

    private sealed class StubTokens : ISessionTokenSource
    {
        private readonly Queue<SessionToken?> _queue;
        public StubTokens(params SessionToken?[] tokens) => _queue = new Queue<SessionToken?>(tokens);
        public int AcquireCount { get; private set; }
        public int InvalidateCount { get; private set; }
        public string? LastDetail => "stub";

        public Task<SessionToken?> AcquireAsync(CancellationToken cancellationToken)
        {
            AcquireCount++;
            return Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : null);
        }

        public void Invalidate() => InvalidateCount++;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task Answer_is_returned_verbatim_and_posted_as_message()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"answer":"Два плюс два — четыре.","source":"model"}"""));
        var tokens = new StubTokens(new SessionToken("tok-1", Fresh: false));
        using var client = new CoreChatClient(Options, tokens, new HttpClient(handler));

        ChatOutcome outcome = await client.SendAsync("сколько будет два плюс два", CancellationToken.None);

        Assert.True(outcome.IsSuccess);
        Assert.Equal("Два плюс два — четыре.", outcome.Answer);
        Assert.Equal("http://127.0.0.1:5080/chat", handler.Seen[0].Uri);
        Assert.Contains("\"message\":", handler.LastBody);
        Assert.Contains("сколько будет два плюс два", handler.LastBody);
        Assert.Equal("tok-1", handler.Seen[0].Auth);
    }

    [Fact]
    public async Task Unreachable_core_reports_unavailable_not_an_answer()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused to 127.0.0.1:5080"));
        var tokens = new StubTokens(new SessionToken("tok-1", Fresh: false));
        using var client = new CoreChatClient(Options, tokens, new HttpClient(handler));

        ChatOutcome outcome = await client.SendAsync("привет", CancellationToken.None);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ChatStatus.Unavailable, outcome.Status);
        Assert.Null(outcome.Answer);
        // The transport message must never reach the UI (it embeds the endpoint).
        Assert.DoesNotContain("127.0.0.1", outcome.Detail);
    }

    [Fact]
    public async Task Missing_session_reports_unavailable_without_calling_core()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"answer":"не должно быть вызвано"}"""));
        var tokens = new StubTokens((SessionToken?)null);
        using var client = new CoreChatClient(Options, tokens, new HttpClient(handler));

        ChatOutcome outcome = await client.SendAsync("привет", CancellationToken.None);

        Assert.Equal(ChatStatus.Unavailable, outcome.Status);
        Assert.Empty(handler.Seen);
    }

    [Fact]
    public async Task Fresh_token_rejection_is_terminal_without_retry()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Unauthorized, """{"error":"invalid_session"}"""));
        var tokens = new StubTokens(new SessionToken("tok-fresh", Fresh: true));
        using var client = new CoreChatClient(Options, tokens, new HttpClient(handler));

        ChatOutcome outcome = await client.SendAsync("привет", CancellationToken.None);

        Assert.Equal(ChatStatus.AuthRejected, outcome.Status);
        Assert.Single(handler.Seen);
        Assert.Equal(0, tokens.InvalidateCount);
    }

    [Fact]
    public async Task Cached_token_rejection_retries_once_with_a_fresh_token()
    {
        int calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return calls == 1
                ? Json(HttpStatusCode.Unauthorized, "{}")
                : Json(HttpStatusCode.OK, """{"answer":"после обновления"}""");
        });
        var tokens = new StubTokens(
            new SessionToken("tok-old", Fresh: false),
            new SessionToken("tok-new", Fresh: true));
        using var client = new CoreChatClient(Options, tokens, new HttpClient(handler));

        ChatOutcome outcome = await client.SendAsync("привет", CancellationToken.None);

        Assert.True(outcome.IsSuccess);
        Assert.Equal("после обновления", outcome.Answer);
        Assert.Equal(2, handler.Seen.Count);
        Assert.Equal(1, tokens.InvalidateCount);
        Assert.Equal("tok-new", handler.Seen[1].Auth);
    }

    [Fact]
    public async Task Answerless_body_is_malformed_never_rendered()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"source":"model"}"""));
        var tokens = new StubTokens(new SessionToken("tok", Fresh: false));
        using var client = new CoreChatClient(Options, tokens, new HttpClient(handler));

        ChatOutcome outcome = await client.SendAsync("привет", CancellationToken.None);

        Assert.Equal(ChatStatus.Malformed, outcome.Status);
        Assert.Null(outcome.Answer);
    }

    [Fact]
    public async Task Empty_answer_is_malformed_not_a_blank_bubble()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"answer":""}"""));
        var tokens = new StubTokens(new SessionToken("tok", Fresh: false));
        using var client = new CoreChatClient(Options, tokens, new HttpClient(handler));

        ChatOutcome outcome = await client.SendAsync("привет", CancellationToken.None);

        Assert.Equal(ChatStatus.Malformed, outcome.Status);
    }

    [Fact]
    public async Task Non_json_body_is_malformed()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>gateway</html>", Encoding.UTF8, "text/html"),
        });
        var tokens = new StubTokens(new SessionToken("tok", Fresh: false));
        using var client = new CoreChatClient(Options, tokens, new HttpClient(handler));

        ChatOutcome outcome = await client.SendAsync("привет", CancellationToken.None);

        Assert.Equal(ChatStatus.Malformed, outcome.Status);
        Assert.Null(outcome.Answer);
    }

    [Fact]
    public async Task Service_unavailable_names_the_model_not_a_guessed_reason()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.ServiceUnavailable, """{"error":"model_unavailable"}"""));
        var tokens = new StubTokens(new SessionToken("tok", Fresh: false));
        using var client = new CoreChatClient(Options, tokens, new HttpClient(handler));

        ChatOutcome outcome = await client.SendAsync("привет", CancellationToken.None);

        Assert.Equal(ChatStatus.Rejected, outcome.Status);
        Assert.Contains("модель", outcome.Detail);
    }

    [Fact]
    public async Task Empty_message_never_reaches_core()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"answer":"x"}"""));
        var tokens = new StubTokens(new SessionToken("tok", Fresh: false));
        using var client = new CoreChatClient(Options, tokens, new HttpClient(handler));

        ChatOutcome outcome = await client.SendAsync("   ", CancellationToken.None);

        Assert.Equal(ChatStatus.Malformed, outcome.Status);
        Assert.Empty(handler.Seen);
    }
}
