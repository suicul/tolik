namespace TOLIK.Infrastructure.Authorization;

/// <summary>
/// Emergency Stop state (docs/governance.md §8, Phase G — PLAN REFORM PRG-4a): the
/// owner's SOFT kill switch — a MANAGED PAUSE of autonomy, deliberately different
/// from LOCKED (which is a security incident state that revokes sessions and reacts
/// to tamper). STOP: the active autonomous/background tasks PAUSE; the NEW PRIVILEGED
/// operations DENY (the reads stay — the owner keeps the channel to chat/ask/resume);
/// identity / memory / Genesis / LIFETIME are NEVER touched. The resume is the owner's
/// explicit authorization (the existing model). In-memory by design: an emergency
/// stop is a RUNTIME pause, not a persisted identity state — a restart is the natural
/// reset and the owner can re-stop (documented, docs/governance.md §8).
/// Thread-safe; never authorizes anything — it only ever RESTRICTS.
/// </summary>
public sealed class EmergencyStopState
{
    private volatile bool _stopped;

    public bool IsStopped => _stopped;

    /// <summary>The owner's STOP — always safe to stop (the safety direction).</summary>
    public void Stop() => _stopped = true;

    /// <summary>The owner's explicit RESUME — re-enables autonomy (the deliberate direction).</summary>
    public void Resume() => _stopped = false;
}
