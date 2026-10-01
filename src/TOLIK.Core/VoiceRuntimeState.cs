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

    public VoiceRuntimeMode Current => _mode;
    public DateTimeOffset ChangedAt => _changedAt;
    public string? ChangedReason => _changedReason;

    /// <summary>Validated mode switch. Unknown values fail closed to Normal with false return.</summary>
    public bool TrySwitch(string? value, string? reason = null)
    {
        var parsed = value?.Trim().ToLowerInvariant() switch
        {
            "normal" or "обычный" => VoiceRuntimeMode.Normal,
            "gaming" or "игровой" => VoiceRuntimeMode.Gaming,
            "gamingassist" => VoiceRuntimeMode.GamingAssist,
            "night" or "ночной" or "ночь" => VoiceRuntimeMode.Night,
            _ => (VoiceRuntimeMode?)null
        };
        if (parsed is null) return false;
        _mode = parsed.Value;
        _changedAt = DateTimeOffset.UtcNow;
        _changedReason = reason;
        return true;
    }

    /// <summary>Snapshot for responses and /status.</summary>
    public (VoiceRuntimeMode Mode, DateTimeOffset ChangedAt, string? Reason) Snapshot() => (_mode, _changedAt, _changedReason);
}
