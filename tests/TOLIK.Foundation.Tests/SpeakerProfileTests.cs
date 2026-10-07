using TOLIK.Core;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Speaker profile v2 (docs/voice-identity.md VI-1): canonical anchors stay immutable,
/// the adaptive layer is bounded/aging and only extends recognition; a reset restores
/// the pristine enrollment; v1 profiles (a bare embedding) keep loading.
/// Thresholds are VI-0-calibrated constants asserted here so silent drift fails tests.
/// </summary>
public sealed class SpeakerProfileTests
{
    private static readonly IReadOnlyList<double> AnchorA = [0.8, 0.6, 0.0, 0.0];
    private static readonly IReadOnlyList<double> AnchorB = [0.6, 0.8, 0.0, 0.0];

    private static SpeakerProfile Enroll() => new([AnchorA, AnchorB]);

    private static void AssertAlmostEqual(IReadOnlyList<double> a, IReadOnlyList<double> b, int precision = 6)
    {
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
            Assert.Equal(a[i], b[i], precision);
    }

    [Fact]
    public void V1_profile_loads_as_canonical_single()
    {
        var stored = $$"""{"embedding":[0.5,0.5,0.5,0.5],"dim":4,"model":"campplus","enrolledAt":"2026-10-07T00:00:00Z"}""";
        var profile = SpeakerProfile.TryParse(stored);
        Assert.NotNull(profile);
        Assert.Single(profile!.Canonical);
        Assert.Empty(profile.Adaptive);
        // Unit-normalized: 0.5,0.5,0.5,0.5 has norm 1.0 -> unchanged.
        Assert.Equal(0.5, profile.CanonicalMean[0], 6);
    }

    [Fact]
    public void V2_roundtrip_preserves_canonical_and_adaptive()
    {
        var original = Enroll().AddAdaptive([0.0, 0.0, 1.0, 0.0], DateTimeOffset.UtcNow);
        var restored = SpeakerProfile.TryParse(original.ToJson());
        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Canonical.Count);
        Assert.Single(restored.Adaptive);
        Assert.Equal(original.Adaptive[0].AddedAt, restored.Adaptive[0].AddedAt);
        Assert.Equal(original.BlendedMean, restored.BlendedMean);
    }

    [Fact]
    public void Similarity_is_max_of_canonical_and_blend()
    {
        var profile = Enroll().AddAdaptive([0.0, 0.0, 1.0, 0.0], DateTimeOffset.UtcNow);
        // A vector living in the adaptive direction: low via canonical, higher via blend.
        var adaptiveState = new[] { 0.0, 0.0, 1.0, 0.0 };
        var viaCanonical = SpeakerVerifier.Cosine(adaptiveState, profile.CanonicalMean);
        var viaBlend = SpeakerVerifier.Cosine(adaptiveState, profile.BlendedMean);
        Assert.Equal(Math.Max(viaCanonical, viaBlend), profile.Similarity(adaptiveState), 9);
        Assert.True(viaBlend > viaCanonical); // the adaptive layer genuinely extended recognition
    }

    [Fact]
    public void Adaptive_cap_evicts_oldest()
    {
        var profile = Enroll();
        for (var i = 0; i < SpeakerProfile.AdaptiveSampleCap + 3; i++)
            profile = profile.AddAdaptive([0.1 * i, 0.1, 0.9, 0.0], DateTimeOffset.UtcNow.AddDays(i));
        Assert.Equal(SpeakerProfile.AdaptiveSampleCap, profile.Adaptive.Count);
        // The three oldest (days 0,1,2) are gone; the newest survives.
        Assert.DoesNotContain(profile.Adaptive, s => (s.AddedAt - DateTimeOffset.UtcNow).TotalDays < 2.9);
    }

    [Fact]
    public void Expired_adaptive_samples_leave_the_blend()
    {
        var now = DateTimeOffset.UtcNow;
        var profile = Enroll().AddAdaptive([0.0, 0.0, 1.0, 0.0], now.AddDays(-31));
        AssertAlmostEqual(profile.CanonicalMean, profile.BlendedMeanAt(now)); // blend == canonical
        var fresh = Enroll().AddAdaptive([0.0, 0.0, 1.0, 0.0], now);
        Assert.NotEqual(fresh.CanonicalMean[2], fresh.BlendedMeanAt(now)[2]); // a fresh sample pulls it
    }

    [Fact]
    public void Reset_adaptive_restores_pristine_canonical()
    {
        var enrolled = Enroll();
        var adapted = enrolled.AddAdaptive([0.0, 0.0, 1.0, 0.0], DateTimeOffset.UtcNow);
        var reset = adapted.ResetAdaptive();
        Assert.Empty(reset.Adaptive);
        Assert.Equal(enrolled.Canonical, reset.Canonical);       // anchors untouched (owner test #15)
        AssertAlmostEqual(reset.CanonicalMean, reset.BlendedMean); // blend falls back to canonical
    }

    [Fact]
    public void AddAdaptive_never_mutates_canonical()
    {
        var enrolled = Enroll();
        var canonicalBefore = enrolled.Canonical.ToList();
        var adapted = enrolled.AddAdaptive([0.0, 0.0, 1.0, 0.0], DateTimeOffset.UtcNow);
        Assert.Equal(canonicalBefore, enrolled.Canonical);
        Assert.Equal(canonicalBefore, adapted.Canonical);
    }

    [Theory]
    [InlineData(0.61, SpeakerConfidenceTier.High)]
    [InlineData(0.60, SpeakerConfidenceTier.High)]
    [InlineData(0.5999, SpeakerConfidenceTier.Medium)]
    [InlineData(0.45, SpeakerConfidenceTier.Medium)]
    [InlineData(0.4499, SpeakerConfidenceTier.Low)]
    [InlineData(0.10, SpeakerConfidenceTier.Low)]
    public void Tiers_follow_calibrated_boundaries(double similarity, SpeakerConfidenceTier expected)
    {
        Assert.Equal(expected, SpeakerProfile.Tier(similarity));
    }

    [Fact]
    public void Adaptive_candidate_needs_high_tier_and_quality()
    {
        // Owner tests #10/#11/#12: medium/low never train; high needs good quality.
        Assert.True(SpeakerProfile.IsAdaptiveCandidate(0.70, 2500));
        Assert.False(SpeakerProfile.IsAdaptiveCandidate(0.70, 2499)); // quality too low
        Assert.False(SpeakerProfile.IsAdaptiveCandidate(0.50, 4000)); // medium: used, never learned
        Assert.False(SpeakerProfile.IsAdaptiveCandidate(0.30, 4000)); // low/unknown: never
    }

    [Fact]
    public void Parse_rejects_garbage_and_missing_shape()
    {
        Assert.Null(SpeakerProfile.TryParse(null));
        Assert.Null(SpeakerProfile.TryParse(""));
        Assert.Null(SpeakerProfile.TryParse("not json at all"));
        Assert.Null(SpeakerProfile.TryParse("""{"unrelated":true}"""));
        Assert.Null(SpeakerProfile.TryParse("""{"version":2,"canonical":[],"adaptive":[]}""")); // no anchors
    }

    [Fact]
    public void Constructor_validates_vectors()
    {
        Assert.Throws<ArgumentException>(() => new SpeakerProfile([]));
        Assert.Throws<ArgumentException>(() => new SpeakerProfile([[1.0, 2.0], [1.0]])); // dim mismatch
        Assert.Throws<ArgumentException>(() => new SpeakerProfile([[0.0, 0.0, 0.0, 0.0]])); // zero norm
        Assert.Throws<ArgumentException>(() => Enroll().AddAdaptive([1.0, 0.0], DateTimeOffset.UtcNow)); // wrong dim
    }

    [Fact]
    public void Canonical_anchor_weight_and_tiers_are_calibrated_constants()
    {
        // VI-0 (docs/voice-identity.md §3): any silent drift of the calibrated numbers fails here.
        Assert.Equal(0.7, SpeakerProfile.CanonicalAnchorWeight);
        Assert.Equal(0.60, SpeakerProfile.AdaptiveTier);
        Assert.Equal(SpeakerVerifier.AcceptThreshold, SpeakerProfile.AcceptTier);
        Assert.Equal(2500, SpeakerProfile.MinAdaptiveQualitySpeechMs);
        Assert.Equal(8, SpeakerProfile.AdaptiveSampleCap);
        Assert.Equal(30.0, SpeakerProfile.AdaptiveSampleTtl.TotalDays);
    }
}
