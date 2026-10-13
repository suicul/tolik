using TOLIK.Desktop.Bridge;

namespace TOLIK.Desktop.Speech;

/// <summary>
/// Presentation policy for the mind's voice (proactive + presence_return — both are
/// «the mind speaks», one surface): decides WHAT may be spoken NOW, what waits, and
/// what is honestly dropped. Pure, clock-free, fully testable headless; the WPF band
/// (<see cref="CephalonSpeechBand"/>) owns only the choreography and reports the
/// cycle end back via <see cref="NotifyCycleCompleted"/>.
///
/// Honesty rules:
///   • EMERGENCY STOP (the real Core /health fact, fed by the App) = honest silence
///     for proactive lines — the mind is paused, it does not make suggestions.
///     presence_return still shows: a digest of the past is still true.
///   • engaging the stop also drops QUEUED proactive lines: stale advice from a
///     paused mind must not surface later. The line already ON the band finishes
///     its cycle — cutting a spoken sentence mid-word would be the hard-swap this
///     surface exists to avoid;
///   • a line arriving mid-cycle never hard-swaps over a visible one — it queues;
///   • the queue holds at most <see cref="MaxQueued"/>; overflow honestly drops the
///     OLDEST (the newest facts are the ones a returning owner needs);
///   • an identical re-push (same kind + summary — presence_return arrives on every
///     reconnect) while the same words are showing or waiting is the SAME fact, not
///     new speech: spoken once, not twice.
///
/// All methods run on the UI thread (the App marshals the worker-thread events).
/// </summary>
public sealed class SpeechPresenter
{
    /// <summary>How many lines may wait behind the one showing. Small by design —
    /// the mind speaks in sentences, not in backlogs.</summary>
    public const int MaxQueued = 3;

    private readonly Queue<ProactiveMessage> _queue = new();
    private ProactiveMessage? _showing;
    private bool _emergencyStopped;

    /// <summary>A line was accepted for display — the band starts its cycle.</summary>
    public event Action<ProactiveMessage>? SpeechStarted;

    /// <summary>The line currently on the band (null while idle) — diagnostics/tests.</summary>
    public ProactiveMessage? Showing => _showing;

    /// <summary>True while a line is on the band — diagnostics/tests.</summary>
    public bool IsShowing => _showing is not null;

    public int QueuedCount => _queue.Count;

    public bool EmergencyStopped => _emergencyStopped;

    /// <summary>Feeds the real emergency-stop transition (worker event, marshaled by
    /// the App). Engaging the stop purges queued proactive lines (see above).</summary>
    public void SetEmergencyStopped(bool stopped)
    {
        if (_emergencyStopped == stopped)
        {
            return;
        }
        _emergencyStopped = stopped;
        if (!stopped)
        {
            return;
        }

        // Honest silence: queued proactive advice is stale the moment the mind
        // pauses. Presence digests stay (in order) — they may still be shown.
        int count = _queue.Count;
        for (int i = 0; i < count; i++)
        {
            ProactiveMessage queued = _queue.Dequeue();
            if (queued.Kind == MessageKind.PresenceReturn)
            {
                _queue.Enqueue(queued);
            }
        }
    }

    /// <summary>A parsed mind-voice message arrived (UI thread).</summary>
    public void OnMessage(ProactiveMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Kind == MessageKind.Proactive && _emergencyStopped)
        {
            return; // the mind is paused — honest silence
        }
        if (IsRepeatOfKnown(message))
        {
            return; // the same fact re-pushed is not new speech
        }

        if (_showing is null)
        {
            Start(message);
            return;
        }

        if (_queue.Count >= MaxQueued)
        {
            _queue.Dequeue(); // overflow drops the OLDEST, honestly
        }
        _queue.Enqueue(message);
    }

    /// <summary>The band finished a full cycle (fade-out done). The next queued line,
    /// if any, begins its OWN cycle — nothing ever hard-swaps over a visible line.</summary>
    public void NotifyCycleCompleted()
    {
        _showing = null;
        while (_queue.Count > 0)
        {
            ProactiveMessage next = _queue.Dequeue();
            if (IsRepeatOfKnown(next))
            {
                continue; // a queued line aged into a repeat of what was just shown
            }
            Start(next);
            return;
        }
    }

    private void Start(ProactiveMessage message)
    {
        _showing = message;
        SpeechStarted?.Invoke(message);
    }

    private bool IsRepeatOfKnown(ProactiveMessage message) =>
        (_showing is { } showing && SameFact(showing, message))
        || _queue.Any(queued => SameFact(queued, message));

    private static bool SameFact(ProactiveMessage a, ProactiveMessage b) =>
        a.Kind == b.Kind && string.Equals(a.Summary, b.Summary, StringComparison.Ordinal);
}
