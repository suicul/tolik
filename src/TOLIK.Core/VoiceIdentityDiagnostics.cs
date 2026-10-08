namespace TOLIK.Core;

/// <summary>One adaptive sample as seen by diagnostics: age and weight only — never the vector.</summary>
public sealed record AdaptiveSampleInfo(double AgeDays, double Weight);

/// <summary>Calibrated identity thresholds (docs/voice-identity.md §3, VI-0).</summary>
public sealed record VoiceIdentityThresholds(
    double AcceptTier,
    double AdaptiveTier,
    int MinSpeechMs,
    int Cap,
    double TtlDays);

/// <summary>
/// Speaker identity diagnostic snapshot (docs/voice-identity.md §9, VI-5).
/// Carries counts, ages, weights and thresholds only — no text content,
/// embeddings are never included.
/// </summary>
public sealed record VoiceIdentityReport(
    Guid UserId,
    bool Found,
    string? Model,
    DateTimeOffset? EnrolledAt,
    int CanonicalCount,
    int AdaptiveCount,
    IReadOnlyList<AdaptiveSampleInfo> Adaptive,
    double? DriftCosine,
    VoiceIdentityThresholds Thresholds,
    bool AdaptationEnabled);

/// <summary>
/// Pure diagnostics builder over a stored "speaker.profile" payload (v1/v2 aware).
/// Null or unreadable payloads report <c>Found=false</c> with zero counts and null drift.
/// </summary>
public static class VoiceIdentityDiagnostics
{
    public static VoiceIdentityReport Build(Guid userId, string? storedProfile, bool adaptationEnabled)
    {
        var thresholds = new VoiceIdentityThresholds(
            SpeakerProfile.AcceptTier,
            SpeakerProfile.AdaptiveTier,
            SpeakerProfile.MinAdaptiveQualitySpeechMs,
            SpeakerProfile.AdaptiveSampleCap,
            SpeakerProfile.AdaptiveSampleTtl.TotalDays);
        var profile = SpeakerProfile.TryParse(storedProfile);
        if (profile is null)
            return new VoiceIdentityReport(userId, false, null, null, 0, 0, [], null, thresholds, adaptationEnabled);
        var now = DateTimeOffset.UtcNow;
        var adaptive = profile.Adaptive
            .Select(s => new AdaptiveSampleInfo((now - s.AddedAt).TotalDays, s.Weight))
            .ToList();
        double? drift = null;
        if (profile.Adaptive.Count > 0)
        {
            try { drift = SpeakerVerifier.Cosine(profile.CanonicalMean, profile.BlendedMean); }
            catch (ArgumentException) { drift = null; }
        }
        return new VoiceIdentityReport(
            userId, true, profile.Model, profile.EnrolledAt,
            profile.Canonical.Count, profile.Adaptive.Count, adaptive,
            drift, thresholds, adaptationEnabled);
    }
}
