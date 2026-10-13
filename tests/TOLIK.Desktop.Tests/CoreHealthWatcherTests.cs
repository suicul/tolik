using System.Net;
using System.Net.Http;
using TOLIK.Desktop.Bridge;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// The emergency-stop feed (the honesty gate behind the mind's voice), verified
/// headless: the pure /health extraction is strict about shape but never mutes the
/// mind over unreadable metadata (unknown → "not stopped"), and the poll loop
/// raises ONLY real transitions on a worker thread, keeping the last known state
/// through failed polls (an unreachable Core also means no messages to display).
/// </summary>
public class CoreHealthWatcherTests
{
    private static CoreLinkOptions Options() =>
        CoreLinkOptions.FromEnvironment(raw: "http://127.0.0.1:5080");

    // ---------- pure extraction ----------

    [Theory]
    [InlineData("{\"core\":true,\"voice\":false,\"agent\":true,\"memory\":true," +
                "\"locked\":false,\"emergencyStopped\":true,\"coreLease\":{}}", true)]
    [InlineData("{\"core\":true,\"emergencyStopped\":false}", false)]
    [InlineData("{\"emergencyStopped\":true}", true)]
    public void Health_body_extracts_the_stop_fact(string body, bool expected)
    {
        Assert.True(HealthWireReader.TryParseEmergencyStopped(body, out bool stopped));
        Assert.Equal(expected, stopped);
    }

    [Theory]
    [InlineData("{}")]                                  // older Core — fact not published
    [InlineData("{\"core\":true}")]                     // field missing
    [InlineData("{\"emergencyStopped\":\"yes\"}")]      // non-bool
    [InlineData("{\"emergencyStopped\":null}")]         // null
    [InlineData("not json at all")]                     // garbage
    [InlineData("[1,2,3]")]                             // not an object
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unreadable_state_is_unknown_never_a_guess(string? body)
    {
        Assert.False(HealthWireReader.TryParseEmergencyStopped(body, out bool stopped));
        Assert.False(stopped); // unknown → not stopped (display gate, fail-open by design)
    }

    [Fact]
    public void Health_uri_is_derived_from_the_core_base()
    {
        Assert.Equal("http://127.0.0.1:5080/health", Options().HealthUri.ToString());
    }

    // ---------- poll loop (scripted transport) ----------

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(int Status, string Body)> _responses = new();
        public int Calls { get; private set; }

        public ScriptedHandler(params (int Status, string Body)[] responses)
        {
            foreach (var (status, body) in responses)
            {
                _responses.Enqueue((status, body));
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            (int status, string body) = _responses.Count > 0
                ? _responses.Dequeue()
                : (200, "{}");
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body),
            });
        }
    }

    /// <summary>Builds a watcher over scripted transport; the injected delay honors
    /// cancellation and lets each test count its polls to bound the run. The HttpClient
    /// intentionally outlives this method — the watcher uses it for the whole run.</summary>
    private static CoreHealthWatcher CreateWatcher(
        HttpMessageHandler handler,
        CancellationTokenSource cts,
        List<bool> events,
        int pollsUntilCancel)
    {
        int polls = 0;
        var http = new HttpClient(handler);
        var watcher = new CoreHealthWatcher(
            Options(),
            pollInterval: TimeSpan.Zero,
            http: http,
            delay: (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                polls++;
                if (polls >= pollsUntilCancel)
                {
                    cts.Cancel();
                }
                return Task.CompletedTask;
            });
        watcher.EmergencyStopChanged += stopped =>
        {
            lock (events)
            {
                events.Add(stopped);
            }
        };
        return watcher;
    }

    [Fact]
    public async Task Poll_loop_raises_only_real_transitions()
    {
        var handler = new ScriptedHandler(
            (200, "{\"emergencyStopped\":false}"),  // initial state — no event
            (200, "{\"emergencyStopped\":true}"),   // transition → event
            (200, "{\"emergencyStopped\":true}"),   // same value — no event
            (500, "server error"),                  // failed poll — keeps last known
            (200, "{\"emergencyStopped\":false}")); // transition back → event

        var events = new List<bool>();
        using var cts = new CancellationTokenSource();
        CoreHealthWatcher watcher = CreateWatcher(handler, cts, events, pollsUntilCancel: 5);

        await watcher.RunAsync(cts.Token);

        Assert.Equal(5, ((ScriptedHandler)handler).Calls);
        Assert.Equal([true, false], events); // exactly the two real transitions
        Assert.False(watcher.EmergencyStopped);
    }

    [Fact]
    public async Task Repeated_same_state_never_spams_events()
    {
        var handler = new ScriptedHandler(
            (200, "{\"emergencyStopped\":true}"),
            (200, "{\"emergencyStopped\":true}"),
            (200, "{\"emergencyStopped\":true}"));

        var events = new List<bool>();
        using var cts = new CancellationTokenSource();
        CoreHealthWatcher watcher = CreateWatcher(handler, cts, events, pollsUntilCancel: 3);

        await watcher.RunAsync(cts.Token);

        Assert.Equal([true], events); // one event: the first establishment of the state
    }

    [Fact]
    public async Task Unreachable_core_produces_no_events_and_no_crash()
    {
        var events = new List<bool>();
        using var cts = new CancellationTokenSource();
        CoreHealthWatcher watcher = CreateWatcher(new ThrowingHandler(), cts, events, pollsUntilCancel: 3);

        await watcher.RunAsync(cts.Token);

        Assert.Empty(events);
        Assert.False(watcher.EmergencyStopped);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("core unreachable"));
        }
    }
}
