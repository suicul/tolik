namespace TOLIK.Core;

/// <summary>
/// Global adaptive-learning toggle (docs/voice-identity.md §9, VI-5), default ON.
/// Owner-only flips via API; the service AND the turn path both consult it
/// (defense in depth).
/// </summary>
public sealed class VoiceAdaptationPolicy
{
    private volatile bool _enabled = true;

    public bool Enabled => _enabled;

    public void SetEnabled(bool enabled) => _enabled = enabled;
}
