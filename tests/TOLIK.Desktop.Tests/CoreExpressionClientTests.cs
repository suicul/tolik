using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using TOLIK.Desktop.Bridge;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// The client loop contract (CE-2), verified headless against scripted sockets:
/// connect → parse only cephalon_expression frames → honest state transitions;
/// transport failures reconnect with capped backoff; 401/403 against a FRESH
/// token is TERMINAL (never a blind reconnect loop against a Core telling the
/// truth); a cached-token 401 earns exactly ONE fresh re-acquisition; the token
/// never leaks into state details.
/// </summary>
public class CoreExpressionClientTests
{
    private const string ValidIdleFrame =
        "{\"type\":\"event\",\"payload\":{\"kind\":\"cephalon_expression\",\"expression\":{" +
        "\"eyeScale\":1.0,\"eyeOpenness\":0.75,\"eyeRotation\":0,\"asymmetry\":0,\"shellExpansion\":0," +
        "\"shellDeformation\":0,\"rotation\":0,\"pulseAmplitude\":0.08,\"glowIntensity\":0.25," +
        "\"particleDensity\":0.1,\"spectrumIntensity\":0,\"movementSpeed\":0.15,\"attentionAngle\":0," +
        "\"attentionIntensity\":0,\"transitionEnergy\":0.3,\"mode\":\"Idle\"},\"at\":\"t\"}}";

    private const string UnknownKindFrame =
        "{\"type\":\"event\",\"payload\":{\"kind\":\"task_started\",\"taskId\":\"7\"}}";

    private const string MalformedExpressionFrame =
        "{\"type\":\"event\",\"payload\":{\"kind\":\"cephalon_expression\",\"expression\":{\"eyeScale\":1.0}}}";

    private static CoreLinkOptions Options() =>
        CoreLinkOptions.FromEnvironment(raw: "http://127.0.0.1:5080");

    // ---------- fakes ----------

    private sealed class FakeTokenSource : ISessionTokenSource
    {
        private readonly Queue<SessionToken?> _queue;
        private readonly SessionToken? _default;

        public FakeTokenSource(SessionToken? fallback, params SessionToken?[] scripted)
        {
            _queue = new Queue<SessionToken?>(scripted);
            _default = fallback;
        }

        public int InvalidateCalls { get; private set; }

        public string? LastDetail => "test: token source exhausted";

        public Task<SessionToken?> AcquireAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : _default);

        public void Invalidate() => InvalidateCalls++;
    }

    private sealed class FakeSocket : IExpressionSocket
    {
        private readonly Queue<string?> _messages;
        private readonly bool _holdOpen;

        public FakeSocket(Exception? connectError, params string?[] messages)
            : this(connectError, holdOpen: false, messages)
        {
        }

        /// <summary>When <paramref name="holdOpen"/> is set, the socket never closes
        /// after its scripted messages — it waits for cancellation (a healthy live
        /// connection), so tests assert steady states without reconnect races.</summary>
        public FakeSocket(Exception? connectError, bool holdOpen, params string?[] messages)
        {
            ConnectError = connectError;
            _holdOpen = holdOpen;
            _messages = new Queue<string?>(messages);
        }

        public Exception? ConnectError { get; }

        public Uri? ConnectedUri { get; private set; }

        public int ConnectCalls { get; private set; }

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            ConnectCalls++;
            ConnectedUri = uri;
            if (ConnectError is not null)
            {
                throw ConnectError;
            }
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
        {
            if (_messages.Count > 0)
            {
                return _messages.Dequeue();
            }
            if (_holdOpen)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            return null;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedSockets
    {
        private readonly Queue<FakeSocket> _sockets = new();
        public int Created { get; private set; }

        public void Add(FakeSocket socket) => _sockets.Enqueue(socket);

        public Func<IExpressionSocket> Factory => () =>
        {
            Created++;
            if (_sockets.Count == 0)
            {
                // A runaway loop must be visible, not silent.
                return new FakeSocket(new InvalidOperationException("test: no more scripted sockets"));
            }
            return _sockets.Dequeue();
        };
    }

    private sealed class DelayRecorder
    {
        private readonly List<TimeSpan> _delays = new();

        public Func<TimeSpan, CancellationToken, Task> Func => (delay, ct) =>
        {
            lock (_delays)
            {
                _delays.Add(delay);
            }
            // A real (tiny) delay so the async loop yields and tests can observe.
            return Task.Delay(1, ct);
        };

        public List<TimeSpan> Snapshot()
        {
            lock (_delays)
            {
                return new List<TimeSpan>(_delays);
            }
        }
    }

    private sealed record LinkHarness(
        CoreExpressionClient Client,
        List<ExpressionFrame> Received,
        List<LinkSnapshot> States);

    private static LinkHarness Build(
        ISessionTokenSource tokens,
        Func<IExpressionSocket> factory,
        DelayRecorder delays)
    {
        var received = new List<ExpressionFrame>();
        var states = new List<LinkSnapshot>();
        var client = new CoreExpressionClient(Options(), tokens, factory, delay: delays.Func);
        client.FrameReceived += f => { lock (received) { received.Add(f); } };
        client.StateChanged += s => { lock (states) { states.Add(s); } };
        return new LinkHarness(client, received, states);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("condition not reached within timeout");
    }

    private static async Task RunUntilCompleteAsync(Func<Task> runFactory, int timeoutMs = 5000)
    {
        Task run = runFactory();
        Task finished = await Task.WhenAny(run, Task.Delay(timeoutMs));
        if (finished != run)
        {
            Assert.Fail("RunAsync did not complete within timeout");
        }
        await run; // must not throw (§40)
    }

    // ---------- the loop ----------

    [Fact]
    public async Task Valid_frames_flow_and_other_traffic_is_safely_skipped()
    {
        var sockets = new ScriptedSockets();
        sockets.Add(new FakeSocket(null, ValidIdleFrame, UnknownKindFrame, MalformedExpressionFrame, null));
        sockets.Add(new FakeSocket(null, holdOpen: true, ValidIdleFrame));
        var delays = new DelayRecorder();
        var tokens = new FakeTokenSource(new SessionToken("tok", Fresh: true));
        LinkHarness harness = Build(tokens, sockets.Factory, delays);
        List<ExpressionFrame> received = harness.Received;

        using var cts = new CancellationTokenSource();
        Task run = harness.Client.RunAsync(cts.Token);

        await WaitForAsync(() => { lock (received) { return received.Count >= 2; } });

        cts.Cancel();
        await run;

        // Only the two valid expression frames reached the animator pipeline —
        // unknown kinds and malformed expression frames are skipped, not fatal.
        lock (received)
        {
            Assert.Equal(2, received.Count);
            Assert.Equal(0.75, received[0].EyeOpenness);
            Assert.Equal(0.25, received[0].GlowIntensity);
        }

        // Honest transitions: connecting → connected → (clean close) → lost → reconnect → connected.
        lock (harness.States)
        {
            Assert.Contains(harness.States, s => s.State == CoreLinkState.Connecting);
            Assert.Contains(harness.States, s => s.State == CoreLinkState.Connected);
            Assert.Contains(harness.States, s => s.State == CoreLinkState.Disconnected
                && s.Detail.Contains("connection lost", StringComparison.OrdinalIgnoreCase));
        }
        Assert.Equal(CoreLinkState.Connected, harness.Client.Snapshot.State);
        Assert.Equal(2, sockets.Created);
    }

    [Fact]
    public async Task Fresh_token_rejected_401_is_terminal_and_never_reconnects()
    {
        var sockets = new ScriptedSockets();
        sockets.Add(new FakeSocket(new WebSocketException("The server returned status code '401': Unauthorized")));
        var delays = new DelayRecorder();
        var tokens = new FakeTokenSource(null, new SessionToken("fresh-token", Fresh: true));
        LinkHarness harness = Build(tokens, sockets.Factory, delays);

        // RunAsync must RETURN by itself (no cancellation) — a Core that denies a
        // fresh credential must not be hammered with reconnect attempts.
        await RunUntilCompleteAsync(() => harness.Client.RunAsync(CancellationToken.None));

        Assert.Equal(CoreLinkState.AuthRejected, harness.Client.Snapshot.State);
        Assert.Contains("auth rejected", harness.Client.Snapshot.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(delays.Snapshot());     // NO backoff at all
        Assert.Equal(1, sockets.Created);     // NO second connect attempt
        Assert.Empty(harness.Received);
    }

    [Fact]
    public async Task Cached_token_rejected_401_earns_exactly_one_fresh_retry()
    {
        var sockets = new ScriptedSockets();
        sockets.Add(new FakeSocket(new HttpRequestException("denied", null, HttpStatusCode.Unauthorized)));
        sockets.Add(new FakeSocket(null, holdOpen: true, ValidIdleFrame));
        var delays = new DelayRecorder();
        var tokens = new FakeTokenSource(
            new SessionToken("fresh-2", Fresh: true),
            new SessionToken("stale-cache", Fresh: false));
        LinkHarness harness = Build(tokens, sockets.Factory, delays);
        List<ExpressionFrame> received = harness.Received;

        using var cts = new CancellationTokenSource();
        Task run = harness.Client.RunAsync(cts.Token);

        await WaitForAsync(() => { lock (received) { return received.Count >= 1; } });

        cts.Cancel();
        await run;

        Assert.Equal(1, tokens.InvalidateCalls);  // the stale cache was dropped once
        Assert.Equal(2, sockets.Created);          // exactly one retry
        Assert.Empty(delays.Snapshot());          // the auth retry is immediate
        lock (harness.States)
        {
            Assert.Contains(harness.States, s => s.State == CoreLinkState.Connecting
                && s.Detail.Contains("re-acquiring", StringComparison.OrdinalIgnoreCase));
        }
        Assert.Equal(CoreLinkState.Connected, harness.Client.Snapshot.State);
        lock (received)
        {
            Assert.Single(received);
        }
    }

    [Fact]
    public async Task Second_401_against_the_fresh_token_is_still_terminal()
    {
        var sockets = new ScriptedSockets();
        sockets.Add(new FakeSocket(new WebSocketException("The server returned status code '403': Forbidden")));
        sockets.Add(new FakeSocket(new WebSocketException("The server returned status code '401': Unauthorized")));
        var delays = new DelayRecorder();
        var tokens = new FakeTokenSource(null,
            new SessionToken("stale", Fresh: false), new SessionToken("fresh", Fresh: true));
        LinkHarness harness = Build(tokens, sockets.Factory, delays);

        await RunUntilCompleteAsync(() => harness.Client.RunAsync(CancellationToken.None));

        Assert.Equal(CoreLinkState.AuthRejected, harness.Client.Snapshot.State);
        Assert.Equal(1, tokens.InvalidateCalls);
        Assert.Equal(2, sockets.Created);
    }

    [Fact]
    public async Task Transport_failures_back_off_with_the_capped_schedule()
    {
        var sockets = new ScriptedSockets();
        for (int i = 0; i < 6; i++)
        {
            sockets.Add(new FakeSocket(new WebSocketException("connection refused")));
        }
        sockets.Add(new FakeSocket(null, holdOpen: true, ValidIdleFrame));
        var delays = new DelayRecorder();
        var tokens = new FakeTokenSource(new SessionToken("tok", Fresh: true));
        LinkHarness harness = Build(tokens, sockets.Factory, delays);
        List<ExpressionFrame> received = harness.Received;

        using var cts = new CancellationTokenSource();
        Task run = harness.Client.RunAsync(cts.Token);

        await WaitForAsync(() => { lock (received) { return received.Count >= 1; } });

        cts.Cancel();
        await run;

        Assert.Equal(
            new[]
            {
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30),
            },
            delays.Snapshot());
        lock (harness.States)
        {
            Assert.Contains(harness.States, s => s.State == CoreLinkState.Disconnected
                && s.Detail.Contains("offline", StringComparison.OrdinalIgnoreCase));
        }
        Assert.Equal(CoreLinkState.Connected, harness.Client.Snapshot.State);
    }

    [Fact]
    public async Task Unavailable_token_is_honest_and_never_touches_the_socket()
    {
        var sockets = new ScriptedSockets();
        var delays = new DelayRecorder();
        var tokens = new FakeTokenSource(null); // nothing available
        LinkHarness harness = Build(tokens, sockets.Factory, delays);

        using var cts = new CancellationTokenSource();
        Task run = harness.Client.RunAsync(cts.Token);

        await WaitForAsync(() => delays.Snapshot().Count >= 2);

        cts.Cancel();
        await run;

        Assert.Equal(0, sockets.Created);
        lock (harness.States)
        {
            Assert.Contains(harness.States, s => s.State == CoreLinkState.Disconnected
                && s.Detail.Contains("no session token", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Cancellation_during_backoff_stops_the_loop_promptly()
    {
        var sockets = new ScriptedSockets();
        sockets.Add(new FakeSocket(new WebSocketException("refused")));
        var delays = new DelayRecorder();
        var tokens = new FakeTokenSource(new SessionToken("tok", Fresh: true));
        LinkHarness harness = Build(tokens, sockets.Factory, delays);

        using var cts = new CancellationTokenSource();
        Task run = harness.Client.RunAsync(cts.Token);

        await WaitForAsync(() => delays.Snapshot().Count >= 1);
        cts.Cancel();

        Task finished = await Task.WhenAny(run, Task.Delay(2000));
        Assert.True(finished == run, "RunAsync must complete promptly after cancellation");
    }

    [Fact]
    public async Task State_details_never_leak_the_token()
    {
        var sockets = new ScriptedSockets();
        sockets.Add(new FakeSocket(
            new WebSocketException("connect failed to ws://x?token=SECRET-TOKEN-VALUE")));
        var delays = new DelayRecorder();
        var tokens = new FakeTokenSource(new SessionToken("SECRET-TOKEN-VALUE", Fresh: true));
        LinkHarness harness = Build(tokens, sockets.Factory, delays);

        using var cts = new CancellationTokenSource();
        Task run = harness.Client.RunAsync(cts.Token);
        await WaitForAsync(() => { lock (harness.States) { return harness.States.Count >= 1; } });
        cts.Cancel();
        await run;

        lock (harness.States)
        {
            Assert.All(harness.States, s => Assert.DoesNotContain("SECRET-TOKEN-VALUE", s.Detail));
            Assert.Contains(harness.States, s => s.Detail.Contains("token=[redacted]"));
        }
    }
}
