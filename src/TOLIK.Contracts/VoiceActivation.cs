using TOLIK.Contracts;

namespace TOLIK.Contracts;

/// <summary>
/// Voice activation parsing and honest resolution (voice-runtime-v2.md 0.9).
/// NO FAKE: Wake mode without a REAL configured wake-word detector falls back to
/// Always with a visible reason — never a pretended detection. Core stays the
/// authority: a mode only gates local capture.
/// </summary>
public static class VoiceActivation
{
    public static VoiceActivationMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "wake" or "wake-word" or "вайк" => VoiceActivationMode.Wake,
        "ptt" or "push-to-talk" or "кнопка" => VoiceActivationMode.PushToTalk,
        _ => VoiceActivationMode.Always
    };

    /// <summary>
    /// Resolves the requested mode against the detector reality. Returns the effective
    /// mode and a fallback reason when the requested one cannot run honestly.
    /// </summary>
    public static (VoiceActivationMode Effective, string? FallbackReason) Resolve(VoiceActivationMode requested, IWakeWordDetector? detector)
    {
        if (requested == VoiceActivationMode.Wake && detector?.IsConfigured != true)
            return (VoiceActivationMode.Always, "wake-word detector not configured; falling back to Always (no fake detection)");
        if (requested == VoiceActivationMode.PushToTalk)
            return (requested, "push-to-talk needs a hotkey binding (desktop client); the console agent gate stays manual");
        return (requested, null);
    }
}

