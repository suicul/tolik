using System.Text.RegularExpressions;

namespace TOLIK.Desktop.Bridge;

/// <summary>Honest connection state of the Core link (rendered verbatim in the
/// debug overlay — "no Core" and "auth rejected" are NEVER allowed to look alike).</summary>
public enum CoreLinkState
{
    Disconnected,
    Connecting,
    Connected,
    AuthRejected,
}

/// <summary>Immutable snapshot of the link state + a token-free human reason.</summary>
public sealed record LinkSnapshot(CoreLinkState State, string Detail)
{
    public static readonly LinkSnapshot Starting = new(CoreLinkState.Disconnected, "starting");
}

/// <summary>
/// CE-2 client wiring: connects the overlay to Core's realtime hub over the EXISTING
/// authenticated WebSocket (/events/ws?token=...), consumes ONLY
/// <c>cephalon_expression</c> payloads and the two MIND-VOICE kinds (proactive /
/// presence_return — the mind speaking to the owner), and drives reconnect policy.
///
/// Honesty rules (the Phase 1 principle, now networked):
///   • no connection → honest idle + "offline" detail;
///   • connected but no expression yet → honest idle + "awaiting expression events";
///   • 401/403 → the debug says AUTH REJECTED — one fresh-token retry is allowed, a
///     rejected FRESH token is terminal (no blind reconnect loop against a Core that
///     is telling the truth);
///   • events flow → the real expression. Nothing is ever fabricated.
///
/// The loop NEVER blocks the UI thread: frames are raised on a worker thread and the
/// app marshals them onto the dispatcher.
/// </summary>
public sealed partial class CoreExpressionClient
{
    private const int MaxDetailLength = 120;

    private readonly CoreLinkOptions _options;
    private readonly ISessionTokenSource _tokens;
    private readonly Func<IExpressionSocket> _socketFactory;
    private readonly ReconnectPolicy _reconnect;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private LinkSnapshot _state = LinkSnapshot.Starting;
    private readonly object _stateGate = new();

    public CoreExpressionClient(
        CoreLinkOptions options,
        ISessionTokenSource tokens,
        Func<IExpressionSocket> socketFactory,
        ReconnectPolicy? reconnect = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
        _reconnect = reconnect ?? new ReconnectPolicy();
        _delay = delay ?? ((ts, ct) => Task.Delay(ts, ct));
    }

    /// <summary>Raised for every parsed expression frame (worker thread).</summary>
    public event Action<ExpressionFrame>? FrameReceived;

    /// <summary>Raised for every parsed mind-voice message — proactive /
    /// presence_return (worker thread). The App marshals onto the dispatcher.</summary>
    public event Action<ProactiveMessage>? MessageReceived;

    /// <summary>Raised on every honest state transition (worker thread).</summary>
    public event Action<LinkSnapshot>? StateChanged;

    public LinkSnapshot Snapshot { get { lock (_stateGate) { return _state; } } }

    /// <summary>Runs until <paramref name="cancellationToken"/> cancels (or auth is
    /// terminally rejected). Never throws — §40: failures surface as honest state.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown — no drama.
        }
        catch (Exception ex)
        {
            // The link must never crash the overlay or die silently: park honestly.
            SetState(CoreLinkState.Disconnected, $"link error — {Describe(ex)}");
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            SessionToken? acquired = await _tokens.AcquireAsync(cancellationToken).ConfigureAwait(false);
            if (acquired is not { } session)
            {
                SetState(CoreLinkState.Disconnected,
                    $"no session token — {_tokens.LastDetail ?? "unknown"}");
                await SleepAsync(_reconnect.NextDelay(), cancellationToken).ConfigureAwait(false);
                continue;
            }

            SetState(CoreLinkState.Connecting, _options.BaseUri.Host);

            using IExpressionSocket socket = _socketFactory();
            try
            {
                // The token is Base64 ('+' and '/') — EscapeDataString is MANDATORY;
                // a raw '+' in the query decodes as a space and the handshake 401s.
                await socket.ConnectAsync(_options.EventsWebSocketUri(session.Value), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                await HandleConnectFailureAsync(ex, session, cancellationToken).ConfigureAwait(false);
                if (Snapshot.State == CoreLinkState.AuthRejected)
                {
                    return; // terminal: a fresh credential was denied — never loop
                }
                continue;
            }

            await ReceiveLoopAsync(socket, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // Clean close or socket fault → transport trouble → backed-off reconnect.
            // (The backoff counter was reset when this connection was established.)
            SetState(CoreLinkState.Disconnected, $"connection lost — {_options.BaseUri.Host}");
            await SleepAsync(_reconnect.NextDelay(), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleConnectFailureAsync(Exception ex, SessionToken session, CancellationToken ct)
    {
        if (WebSocketFailure.Classify(ex) == CoreLinkFailure.AuthRejected)
        {
            if (session.Fresh)
            {
                SetState(CoreLinkState.AuthRejected,
                    $"auth rejected (401/403) — Core denied a fresh dev token; not reconnecting ({_options.BaseUri.Host})");
                return;
            }

            // Cached credential went stale (Core rotated sessions while we ran):
            // exactly ONE fresh-token retry, then the next rejection is terminal.
            _tokens.Invalidate();
            SetState(CoreLinkState.Connecting, "auth rejected — re-acquiring dev token");
            return;
        }

        SetState(CoreLinkState.Disconnected, $"offline — {Describe(ex)} ({_options.BaseUri.Host})");
        await SleepAsync(_reconnect.NextDelay(), ct).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(IExpressionSocket socket, CancellationToken cancellationToken)
    {
        _reconnect.Reset();
        SetState(CoreLinkState.Connected, "connected — awaiting expression events");

        while (!cancellationToken.IsCancellationRequested)
        {
            string? message = await socket.ReceiveTextAsync(cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            if (ExpressionWireParser.TryParse(message, out ExpressionFrame? frame)
                == ExpressionParseOutcome.Parsed && frame is not null)
            {
                FrameReceived?.Invoke(frame);
            }

            if (ExpressionWireParser.TryParseMessage(message, out ProactiveMessage? voice)
                == ExpressionParseOutcome.Parsed && voice is not null)
            {
                MessageReceived?.Invoke(voice);
            }
            // Ignored/Rejected frames keep the connection: other kinds are normal
            // traffic (expression and message parsers coexist, each dispatching on
            // payload.kind); a rejected ours-frame stands down — the last good
            // state stays and the mind says nothing it cannot say whole.
        }
    }

    private async Task SleepAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await _delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Swallowed: the outer loop observes cancellation next iteration.
        }
    }

    private void SetState(CoreLinkState state, string detail)
    {
        LinkSnapshot snapshot;
        lock (_stateGate)
        {
            if (_state.State == state && _state.Detail == detail)
            {
                return;
            }
            _state = new LinkSnapshot(state, detail);
            snapshot = _state;
        }
        StateChanged?.Invoke(snapshot);
    }

    /// <summary>One-line, token-free, length-capped reason for the debug overlay.
    /// Any "token=..." fragment is redacted regardless of source (defense in depth:
    /// the WS URI embeds the credential in its query string).</summary>
    private static string Describe(Exception ex)
    {
        string text = RedactToken().Replace(ex.Message, "token=[redacted]");
        if (text.Length > MaxDetailLength)
        {
            text = text[..MaxDetailLength];
        }
        return text;
    }

    [GeneratedRegex(@"token=[^&\s""']+")]
    private static partial Regex RedactToken();
}
