using System.Net.Http;
using System.Text.Json;

namespace TOLIK.Desktop.Bridge;

/// <summary>
/// Emergency-stop awareness for the desktop presence — the honesty gate behind the
/// mind's voice. Core is the authority (SECURITY §1): the display layer NEVER
/// decides that "the mind is paused" from its own guesswork; it reads the REAL
/// <c>emergencyStopped</c> fact from Core's read-only /health endpoint (anonymous,
/// status-only — it carries no secrets and grants nothing) on a quiet poll.
///
/// Honesty rules:
///   • only a REAL transition is raised, on a worker thread (the App marshals);
///   • a failed poll keeps the last known state — an unreachable Core also means a
///     dead WebSocket, so nothing new could arrive to display anyway; a broken
///     health body must never mute the mind by accident;
///   • a missing field (older Core) reads as "unknown" → not stopped: this is a
///     DISPLAY courtesy gate, not an authorization boundary (AGENTS §10 — the
///     cephalon's subtitles gate nothing but themselves).
/// </summary>
public sealed class CoreHealthWatcher : IDisposable
{
    /// <summary>Quiet by design: the stop state changes at human speed.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    private readonly CoreLinkOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private bool _emergencyStopped;

    public CoreHealthWatcher(
        CoreLinkOptions options,
        TimeSpan? pollInterval = null,
        HttpClient? http = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (http is null)
        {
            _http = new HttpClient(new SocketsHttpHandler
            {
                // Loopback-first dev flow: fail fast so a dead Core costs nothing.
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
        _interval = pollInterval ?? DefaultPollInterval;
        _delay = delay ?? ((ts, ct) => Task.Delay(ts, ct));
    }

    /// <summary>Last known stop state (worker-thread writes; display-grade read).</summary>
    public bool EmergencyStopped => _emergencyStopped;

    /// <summary>Raised on a worker thread ONLY on a real transition.</summary>
    public event Action<bool>? EmergencyStopChanged;

    /// <summary>Polls until cancelled. Never throws — §40: failures keep the last
    /// known honest state instead of crashing the presence.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                bool? stopped = await PollOnceAsync(cancellationToken).ConfigureAwait(false);
                if (stopped is { } value)
                {
                    Apply(value);
                }

                try
                {
                    await _delay(_interval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Normal shutdown — the outer loop observes cancellation next iteration.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown — no drama.
        }
    }

    private async Task<bool?> PollOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response =
                await _http.GetAsync(_options.HealthUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return HealthWireReader.TryParseEmergencyStopped(body, out bool stopped) ? stopped : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Connection refused / DNS / timeout — Core is simply not there. No
            // exception text is stored: it can embed the request URI (defense in
            // depth, same reasoning as LocalTokenSource).
            return null;
        }
    }

    private void Apply(bool stopped)
    {
        if (_emergencyStopped == stopped)
        {
            return;
        }
        _emergencyStopped = stopped;
        EmergencyStopChanged?.Invoke(stopped);
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}

/// <summary>
/// Pure extraction of the emergency-stop fact from a /health body (verified against
/// Core's anonymous object shape: <c>{ core, voice, agent, memory, ..., locked,
/// emergencyStopped, coreLease }</c>). Unknown = TryParse false, stopped stays false:
/// the caller treats "unknown" as not-stopped (a display gate must not mute the
/// mind over metadata it cannot read).
/// </summary>
public static class HealthWireReader
{
    public static bool TryParseEmergencyStopped(string? json, out bool stopped)
    {
        stopped = false;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            if (!root.TryGetProperty("emergencyStopped", out JsonElement value))
            {
                return false; // older Core — the fact is simply not published yet
            }
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }
            stopped = value.GetBoolean();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
