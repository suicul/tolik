using TOLIK.Contracts;

namespace TOLIK.Core;

/// <summary>
/// Global voice runtime mode state (AGENTS.md §14: NORMAL/TACTICAL/ALERT/NIGHT/COMBAT).
/// Switched via chat ("ночной режим") or API; consumed by VoiceRuntimeGate when
/// the request does not carry an explicit mode. Thread-safe singleton.
/// </summary>
public sealed class VoiceRuntimeState
{
    private volatile VoiceRuntimeMode _mode = VoiceRuntimeMode.Normal;
    private DateTimeOffset _changedAt = DateTimeOffset.UtcNow;
    private string? _changedReason;
    private bool _autoGameMode; // the current mode was set by the game auto-switch
    private readonly object _switchLock = new();

    public VoiceRuntimeMode Current => _mode;
    public DateTimeOffset ChangedAt => _changedAt;
    public string? ChangedReason => _changedReason;

    /// <summary>Validated mode switch. Unknown values fail closed to Normal with false return.</summary>
    public bool TrySwitch(string? value, string? reason = null)
    {
        var parsed = value?.Trim().ToLowerInvariant() switch
        {
            "normal" or "обычный" => (VoiceRuntimeMode?)VoiceRuntimeMode.Normal,
            "gaming" or "игровой" => VoiceRuntimeMode.Gaming,
            "gamingassist" => VoiceRuntimeMode.GamingAssist,
            "night" or "ночной" or "ночь" => VoiceRuntimeMode.Night,
            "silent" or "тихий" or "тихо" or "тишина" or "молчи" => VoiceRuntimeMode.Silent,
            _ => (VoiceRuntimeMode?)null
        };
        if (parsed is null) return false;
        lock (_switchLock)
        {
            // A manual switch always cancels the auto game-mode linkage: the owner's
            // explicit choice wins over detection (the auto-restore never fires).
            _autoGameMode = false;
            _mode = parsed.Value;
            _changedAt = DateTimeOffset.UtcNow;
            _changedReason = reason;
        }
        return true;
    }

    /// <summary>
    /// Game mute linkage (voice-runtime-v2.md M8): the agent reports whether a
    /// configured game process is foreground. Normal ⇄ GamingAssist automatically
    /// (listen, assist, never speak into the game). Manual modes (Night/Silent/
    /// Gaming or a manually-set GamingAssist) are NEVER overridden by detection.
    /// Returns true when the mode changed.
    /// </summary>
    public bool ApplyGamePresence(bool gameRunning)
    {
        lock (_switchLock)
        {
            if (gameRunning && _mode == VoiceRuntimeMode.Normal)
            {
                _mode = VoiceRuntimeMode.GamingAssist;
                _autoGameMode = true;
                _changedAt = DateTimeOffset.UtcNow;
                _changedReason = "game detected";
                return true;
            }
            if (!gameRunning && _autoGameMode && _mode == VoiceRuntimeMode.GamingAssist)
            {
                _mode = VoiceRuntimeMode.Normal;
                _autoGameMode = false;
                _changedAt = DateTimeOffset.UtcNow;
                _changedReason = "game exited";
                return true;
            }
            return false;
        }
    }

    /// <summary>Snapshot for responses and /status.</summary>
    public (VoiceRuntimeMode Mode, DateTimeOffset ChangedAt, string? Reason) Snapshot() => (_mode, _changedAt, _changedReason);
}
