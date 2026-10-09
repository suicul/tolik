namespace TOLIK.Contracts;

/// <summary>
/// One speech event with its perception context (docs/identity-presence.md §6, IP-6):
/// a distributed microphone's report — probabilistic, never certain (Speaker=Ivan,
/// Confidence=0.97, not 100%). No raw audio, no transcripts persisted by this type.
/// </summary>
public sealed record SpeechEvent(
    string SourceDevice,
    string? SpeakerCandidate,
    double SpeakerConfidence,
    bool AddressedToTolik,
    string? EmotionEstimate,
    bool OverlapState,
    DateTimeOffset Timestamp)
{
    public static SpeechEvent Unknown(string sourceDevice, DateTimeOffset timestamp) => new(
        sourceDevice, null, 0, AddressedToTolik: false, null, OverlapState: false, timestamp);
}

/// <summary>
/// Address detection (docs/identity-presence.md §6, IP-6): HEARING EVERYTHING IS NOT
/// RESPONDING TO EVERYTHING. Speech falls into: directed-to-TOLIK / between people /
/// TV-movie dialogue / game audio / background conversation / noise — only DIRECTED
/// speech proceeds to intent/attention/response. Deterministic heuristics: the wake
/// word is the primary signal («Толик, ...» — the IntentRouter strips it downstream);
/// a voice turn's own audio is addressed by construction (the user spoke to TOLIK);
/// ambient speech without a wake word is NOT addressed. Deterministic — no model
/// claims; never an authorization input.
/// </summary>
public static class VoiceAddressDetector
{
    /// <summary>Wake words: the primary "addressed to TOLIK" signal.</summary>
    public static readonly string[] WakeWords = ["толик", "tolik", "толь"];

    /// <summary>Deterministic: is this speech addressed to TOLIK?</summary>
    public static bool IsAddressed(string? speechText, bool fromVoiceTurn = false)
    {
        if (fromVoiceTurn) return true; // the voice loop's audio is addressed by construction
        if (string.IsNullOrWhiteSpace(speechText)) return false;
        var lower = speechText.ToLowerInvariant();
        return WakeWords.Any(w => lower.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>Classifies a speech event into the address categories (IP-6).</summary>
    public static SpeechAddressCategory Classify(SpeechEvent @event, string? speechText) =>
        !@event.AddressedToTolik && !IsAddressed(speechText)
            ? SpeechAddressCategory.NotAddressed
            : @event.OverlapState ? SpeechAddressCategory.Overlapping
            : SpeechAddressCategory.Direct;
}

public enum SpeechAddressCategory
{
    /// <summary>Directed to TOLIK, no overlap — proceeds to intent/response.</summary>
    Direct,
    /// <summary>Addressed but overlapping with another speaker — arbitration needed.</summary>
    Overlapping,
    /// <summary>Not addressed (between people / TV / game / noise) — heard, not responded to.</summary>
    NotAddressed
}

/// <summary>
/// Attention arbitration (docs/identity-presence.md §6, IP-6): NOT last-speaker-wins.
/// Ranks competing speech events by: explicit address (wake word) > speaker
/// confidence > urgency signal > current-conversation continuity. The result routes
/// ATTENTION only — never an authorization decision (Attention != Authority).
/// </summary>
public static class AttentionArbiter
{
    public static SpeechEvent? Select(IReadOnlyList<SpeechEvent> candidates, string? currentConversationDevice = null)
    {
        if (candidates is null || candidates.Count == 0) return null;
        var ranked = candidates
            .Select(e => (
                Event: e,
                Score: (e.AddressedToTolik ? 2.0 : 0.0)
                    + e.SpeakerConfidence
                    + (currentConversationDevice is not null &&
                       string.Equals(e.SourceDevice, currentConversationDevice, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0)))
            .OrderByDescending(x => x.Score)
            .ToList();
        return ranked[0].Event;
    }
}
