using System.Net;
using System.Net.Http;
using System.Text;
using TOLIK.Desktop.Bridge;
using TOLIK.Desktop.Voice;
using Xunit;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// Voice transport (CE-5 voice). The client records and plays; Core does everything
/// that decides anything. These tests pin the reading of Core's stream and the
/// honest mapping of every failure — the owner must never hear silence and be told
/// the turn succeeded.
/// </summary>
public sealed class VoiceTurnClientTests
{
    private static readonly CoreLinkOptions Options = new(new Uri("http://127.0.0.1:5080"), "test");
    private static readonly byte[] SomeAudio = [1, 2, 3, 4, 5, 6, 7, 8];

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<(string Uri, string? Body, string? Auth)> Seen { get; } = [];

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Seen.Add((request.RequestUri!.ToString(), body, request.Headers.Authorization?.Parameter));
            return _respond(request);
        }
    }

    private sealed class StubTokens : ISessionTokenSource
    {
        private readonly Queue<SessionToken?> _queue;
        public StubTokens(params SessionToken?[] tokens) => _queue = new Queue<SessionToken?>(tokens);
        public int InvalidateCount { get; private set; }
        public string? LastDetail => "stub";
        public Task<SessionToken?> AcquireAsync(CancellationToken cancellationToken)
            => Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : null);
        public void Invalidate() => InvalidateCount++;
    }

    private static HttpResponseMessage Sse(params string[] events)
    {
        var builder = new StringBuilder();
        foreach (string evt in events)
        {
            builder.Append("data: ").Append(evt).Append("\n\n");
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream"),
        };
    }

    [Fact]
    public async Task Stream_yields_text_and_audio_and_a_transcript()
    {
        var audioBytes = Convert.ToBase64String(new byte[] { 9, 9, 9 });
        var handler = new StubHandler(_ => Sse(
            """{"type":"delta","text":"Включил "}""",
            $$"""{"type":"audio","audio":"{{audioBytes}}"}""",
            """{"type":"delta","text":"музыку."}""",
            """{"type":"done"}"""));
        var tokens = new StubTokens(new SessionToken("tok", Fresh: false));
        using var client = new VoiceTurnClient(Options, tokens, new HttpClient(handler));
        var events = new List<VoiceStreamEvent>();

        VoiceTurnResult result = await client.SendAsync(SomeAudio, events.Add, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(VoiceTurnStatus.Ok, result.Status);
        Assert.Equal("Включил музыку.", result.Transcript);
        Assert.Contains(events, e => e is VoiceStreamEvent.Audio);
        Assert.Equal(2, events.Count(e => e is VoiceStreamEvent.Delta));
        Assert.IsType<VoiceStreamEvent.Done>(events[^1]);
        Assert.Equal("http://127.0.0.1:5080/voice/turn-stream", handler.Seen[0].Uri);
        Assert.Contains("audio", handler.Seen[0].Body);
        Assert.Equal("tok", handler.Seen[0].Auth);
    }

    [Fact]
    public async Task Audio_is_sent_as_base64_of_the_recorded_wav()
    {
        var handler = new StubHandler(_ => Sse("""{"type":"done"}"""));
        var tokens = new StubTokens(new SessionToken("tok", Fresh: false));
        using var client = new VoiceTurnClient(Options, tokens, new HttpClient(handler));

        await client.SendAsync(SomeAudio, null, CancellationToken.None);

        Assert.Contains(Convert.ToBase64String(SomeAudio), handler.Seen[0].Body);
    }

    [Fact]
    public async Task Model_unavailable_on_the_stream_is_a_failure_not_a_quiet_turn()
    {
        var handler = new StubHandler(_ => Sse("""{"type":"error","error":"model_unavailable"}"""));
        var tokens = new StubTokens(new SessionToken("tok", Fresh: false));
        using var client = new VoiceTurnClient(Options, tokens, new HttpClient(handler));

        VoiceTurnResult result = await client.SendAsync(SomeAudio, null, CancellationToken.None);

        Assert.Equal(VoiceTurnStatus.Rejected, result.Status);
        Assert.Contains("модель", result.Detail);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Truncated_stream_is_reported()
    {
        // Deltas without a terminating done: the turn did not finish.
        var handler = new StubHandler(_ => Sse("""{"type":"delta","text":"начал"}"""));
        var tokens = new StubTokens(new SessionToken("tok", Fresh: false));
        using var client = new VoiceTurnClient(Options, tokens, new HttpClient(handler));

        VoiceTurnResult result = await client.SendAsync(SomeAudio, null, CancellationToken.None);

        Assert.Equal(VoiceTurnStatus.Malformed, result.Status);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Silence_is_a_valid_outcome_reported_as_silent()
    {
        var handler = new StubHandler(_ => Sse("""{"type":"done"}"""));
        var tokens = new StubTokens(new SessionToken("tok", Fresh: false));
        using var client = new VoiceTurnClient(Options, tokens, new HttpClient(handler));

        VoiceTurnResult result = await client.SendAsync(SomeAudio, null, CancellationToken.None);

        Assert.Equal(VoiceTurnStatus.Silent, result.Status);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Non_stream_answer_is_read_as_the_intent_path()
    {
        // The documented behaviour: the intent path returns plain JSON, not SSE.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"answer":"Записал напоминание.","transcription":"напомни купить молоко"}""",
                Encoding.UTF8, "application/json"),
        });
        var tokens = new StubTokens(new SessionToken("tok", Fresh: false));
        using var client = new VoiceTurnClient(Options, tokens, new HttpClient(handler));

        VoiceTurnResult result = await client.SendAsync(SomeAudio, null, CancellationToken.None);

        Assert.Equal(VoiceTurnStatus.Ok, result.Status);
        Assert.Equal("Записал напоминание.", result.Transcript);
    }

    [Fact]
    public async Task Pause_answer_from_the_intent_path_is_silent()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"answer":"(пауза) Я ничего не услышал — повтори, пожалуйста."}""",
                Encoding.UTF8, "application/json"),
        });
        var tokens = new StubTokens(new SessionToken("tok", Fresh: false));
        using var client = new VoiceTurnClient(Options, tokens, new HttpClient(handler));

        VoiceTurnResult result = await client.SendAsync(SomeAudio, null, CancellationToken.None);

        Assert.Equal(VoiceTurnStatus.Silent, result.Status);
    }

    [Fact]
    public async Task Empty_recording_never_reaches_core()
    {
        var handler = new StubHandler(_ => Sse("""{"type":"done"}"""));
        var tokens = new StubTokens(new SessionToken("tok", Fresh: false));
        using var client = new VoiceTurnClient(Options, tokens, new HttpClient(handler));

        VoiceTurnResult result = await client.SendAsync([], null, CancellationToken.None);

        Assert.Equal(VoiceTurnStatus.Malformed, result.Status);
        Assert.Empty(handler.Seen);
    }

    [Fact]
    public async Task Missing_session_reports_unavailable_without_a_call()
    {
        var handler = new StubHandler(_ => Sse("""{"type":"done"}"""));
        var tokens = new StubTokens((SessionToken?)null);
        using var client = new VoiceTurnClient(Options, tokens, new HttpClient(handler));

        VoiceTurnResult result = await client.SendAsync(SomeAudio, null, CancellationToken.None);

        Assert.Equal(VoiceTurnStatus.Unavailable, result.Status);
        Assert.Empty(handler.Seen);
    }

    [Fact]
    public async Task Unreachable_core_reports_unavailable_without_leaking_the_endpoint()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("refused 127.0.0.1:5080"));
        var tokens = new StubTokens(new SessionToken("tok", Fresh: false));
        using var client = new VoiceTurnClient(Options, tokens, new HttpClient(handler));

        VoiceTurnResult result = await client.SendAsync(SomeAudio, null, CancellationToken.None);

        Assert.Equal(VoiceTurnStatus.Unavailable, result.Status);
        Assert.DoesNotContain("127.0.0.1", result.Detail);
    }

    [Fact]
    public async Task Cached_token_rejection_retries_once()
    {
        int calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Sse("""{"type":"delta","text":"ok"}""", """{"type":"done"}""");
        });
        var tokens = new StubTokens(
            new SessionToken("old", Fresh: false),
            new SessionToken("new", Fresh: true));
        using var client = new VoiceTurnClient(Options, tokens, new HttpClient(handler));

        VoiceTurnResult result = await client.SendAsync(SomeAudio, null, CancellationToken.None);

        Assert.Equal(VoiceTurnStatus.Ok, result.Status);
        Assert.Equal(2, handler.Seen.Count);
        Assert.Equal("new", handler.Seen[1].Auth);
        Assert.Equal(1, tokens.InvalidateCount);
    }

    [Fact]
    public async Task Fresh_token_rejection_is_terminal()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var tokens = new StubTokens(new SessionToken("fresh", Fresh: true));
        using var client = new VoiceTurnClient(Options, tokens, new HttpClient(handler));

        VoiceTurnResult result = await client.SendAsync(SomeAudio, null, CancellationToken.None);

        Assert.Equal(VoiceTurnStatus.AuthRejected, result.Status);
        Assert.Single(handler.Seen);
    }

    [Fact]
    public async Task Service_unavailable_names_the_voice_stage()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var tokens = new StubTokens(new SessionToken("tok", Fresh: false));
        using var client = new VoiceTurnClient(Options, tokens, new HttpClient(handler));

        VoiceTurnResult result = await client.SendAsync(SomeAudio, null, CancellationToken.None);

        Assert.Equal(VoiceTurnStatus.Rejected, result.Status);
        Assert.Contains("недоступн", result.Detail);
    }
}
