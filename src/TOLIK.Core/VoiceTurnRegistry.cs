using System.Collections.Concurrent;

namespace TOLIK.Core;

/// <summary>
/// Barge-in (voice-runtime-v2.md M5): in-flight voice turns per user. Core owns the
/// registry (SECURITY §7: a client can only cancel its OWN in-flight turn, never
/// another user's). When the user starts a new turn while one is still running, the
/// previous turn cancels automatically (barge-in). The client (Desktop/agent) can
/// also cancel explicitly via POST /voice/cancel. Thread-safe singleton.
/// CTS instances are never disposed here: they carry no timers, and disposal after
/// Cancel would race with linked subscribers still reading Token.
/// </summary>
public sealed class VoiceTurnRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _turns = new();

    /// <summary>
    /// Registers a new in-flight turn for the user. If a previous turn is still in
    /// flight, it is cancelled first (barge-in: the user speaks again). The returned
    /// CTS is cancelled by Cancel(), by a subsequent Register(), or the turn must be
    /// marked finished via Complete() when the stream ends normally.
    /// </summary>
    public CancellationTokenSource Register(Guid userId)
    {
        if (_turns.TryRemove(userId, out var previous))
            previous.Cancel();
        var cts = new CancellationTokenSource();
        _turns[userId] = cts;
        return cts;
    }

    /// <summary>Explicit barge-in: cancels the user's in-flight turn. False when none is running.</summary>
    public bool Cancel(Guid userId)
    {
        if (!_turns.TryRemove(userId, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    /// <summary>Marks the turn finished (removes without cancelling).</summary>
    public void Complete(Guid userId) => _turns.TryRemove(userId, out _);

    /// <summary>
    /// CE-2 (Cephalon expression wiring) read-only probe: is a voice turn currently in
    /// flight for this user — the honest coarse isSpeaking source. Additive; the
    /// registry's own lifecycle API is untouched.
    /// </summary>
    public bool HasInFlightTurn(Guid userId) => _turns.ContainsKey(userId);
}
