using System.Diagnostics;
using System.Globalization;
using TOLIK.Desktop.Motion;

namespace TOLIK.Desktop.Bridge;

/// <summary>
/// UI-side presentation policy for the Core link (CE-2): turns client snapshots
/// and expression frames into animator drive application/clearing plus the honest
/// debug lines. Pure logic — no WPF, no timers, no clock of its own (timestamps
/// come from the injectable provider, the global Stopwatch by default) — fully
/// testable headless.
///
/// The honesty contract (the only states the owner can ever see):
///   1. no connection             → procedural idle, "no Core connection";
///   2. connected, no events yet   → procedural idle, "connected — awaiting expression events";
///   3. auth rejected (401/403)    → procedural idle, "AUTH REJECTED — not reconnecting"
///                                    (worded so it can NEVER read like case 1);
///   4. events flowing            → the REAL expression; core line + last mode + event age.
/// A connected stream that falls silent past <see cref="StaleAfterSeconds"/> drops
/// back to procedural idle — never a frozen expression pretending Core is still
/// talking — and the expression line says so.
/// </summary>
public sealed class ExpressionLinkPresenter
{
    /// <summary>Core heartbeats the idle expression ~1 s and pushes changes ~5 Hz;
    /// anything past this is a dead stream → honest local idle.</summary>
    public const double StaleAfterSeconds = 15.0;

    /// <summary>Debug overlay width guard: reasons are informative, not essays.</summary>
    private const int MaxReasonLength = 52;

    private static readonly string StartingDetail = LinkSnapshot.Starting.Detail;

    private readonly CephalonAnimator _animator;
    private readonly Func<long> _timestamp;

    private CoreLinkState _state = CoreLinkState.Disconnected;
    private string _detail = StartingDetail;
    private long? _lastEventTimestamp;
    private string? _lastMode;
    private bool _driveApplied;
    private bool _stale;

    public ExpressionLinkPresenter(
        CephalonAnimator animator,
        string endpointLabel = "Core",
        Func<long>? timestamp = null)
    {
        _animator = animator ?? throw new ArgumentNullException(nameof(animator));
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        EndpointLabel = endpointLabel ?? "Core";
    }

    /// <summary>Endpoint shown in the connecting/connected debug lines (host[:port]).
    /// Pure display data — never a credential, never a security decision.</summary>
    public string EndpointLabel { get; set; }

    /// <summary>Current link state (mirrors the last snapshot).</summary>
    public CoreLinkState State => _state;

    /// <summary>Last received expression mode, null before the first frame.</summary>
    public string? LastMode => _lastMode;

    /// <summary>Age of the last received frame in seconds, null before the first.</summary>
    public double? LastEventAgeSeconds =>
        _lastEventTimestamp is long ts ? AgeOf(ts) : null;

    /// <summary>True while the animator is driven by a live, non-stale Core stream.</summary>
    public bool DriveApplied => _driveApplied && !_stale;

    /// <summary>Link state snapshot from the client (UI thread). Any state other
    /// than Connected drops the drive: without a stream the body falls back to the
    /// honest procedural idle immediately — never a held pose on dead strings.</summary>
    public void OnLinkState(LinkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _state = snapshot.State;
        _detail = snapshot.Detail;
        if (_state != CoreLinkState.Connected)
        {
            DropDrive();
        }
    }

    /// <summary>A real expression frame from Core (UI thread). Applied immediately —
    /// the animator eases toward the new targets, nothing snaps. Malformed frames
    /// never reach this method (the client rejects them and keeps the last good one).</summary>
    public void OnExpressionFrame(ExpressionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _lastEventTimestamp = _timestamp();
        _lastMode = frame.Mode.ToString();
        _stale = false;
        _animator.ApplyDrive(ExpressionBridge.ToDrive(frame));
        _driveApplied = true;
    }

    /// <summary>Steady tick (the render loop): drops a stale drive and lets the
    /// honest lines age. Cheap — safe to call every frame.</summary>
    public void Tick()
    {
        if (_driveApplied && !_stale
            && LastEventAgeSeconds is double age && age > StaleAfterSeconds)
        {
            _stale = true;
            _animator.ClearDrive();
        }
    }

    /// <summary>Connection truth for the debug overlay — "no Core" and "auth
    /// rejected" are worded so they can never look alike.</summary>
    public string CoreLine => _state switch
    {
        CoreLinkState.Connecting => $"connecting — {EndpointLabel}",
        CoreLinkState.Connected => _lastEventTimestamp is null
            ? "connected — awaiting expression events"
            : $"connected — {EndpointLabel}",
        CoreLinkState.AuthRejected => "AUTH REJECTED — not reconnecting",
        _ => "no Core connection",
    };

    /// <summary>Expression truth for the debug overlay: what the body is actually
    /// doing right now, and (when driven) the mode and age of the last real event.
    /// A disconnected link or an old event from a PREVIOUS connection must never
    /// masquerade as a live expression.</summary>
    public string ExpressionLine
    {
        get
        {
            if (_driveApplied && _stale && _lastEventTimestamp is long staleTs)
            {
                return $"stale — local idle (last {_lastMode ?? "?"} {AgeOf(staleTs).ToString("0", CultureInfo.InvariantCulture)}s ago)";
            }
            if (!DriveApplied || _lastEventTimestamp is not long ts)
            {
                return "procedural idle";
            }
            return $"{_lastMode ?? "?"} · {AgeOf(ts).ToString("0.0", CultureInfo.InvariantCulture)}s ago";
        }
    }

    /// <summary>Token-free reason line (null when there is nothing useful to say).
    /// The client already caps and redacts the detail; this caps it for the overlay.</summary>
    public string? ReasonLine =>
        _state is CoreLinkState.Disconnected or CoreLinkState.AuthRejected
        && !string.IsNullOrEmpty(_detail)
        && _detail != StartingDetail
            ? Truncate(_detail)
            : null;

    private void DropDrive()
    {
        if (_driveApplied)
        {
            _driveApplied = false;
            _stale = false;
            _animator.ClearDrive();
        }
        // The previous connection's events are history: after a disconnect the
        // presenter reports "procedural idle" and (on reconnect) genuinely
        // awaits events — never a stale age ticking up as if the stream lived.
        _lastEventTimestamp = null;
        _lastMode = null;
    }

    private double AgeOf(long timestamp) =>
        (_timestamp() - timestamp) / (double)Stopwatch.Frequency;

    private static string Truncate(string text)
    {
        if (text.Length <= MaxReasonLength)
        {
            return text;
        }
        return text[..(MaxReasonLength - 1)] + "…";
    }
}
