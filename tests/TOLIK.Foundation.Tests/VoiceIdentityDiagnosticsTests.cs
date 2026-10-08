using TOLIK.Core;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Voice identity diagnostics (docs/voice-identity.md §9, VI-5): the report carries
/// counts, sample ages/weights, drift and thresholds — never text or embeddings.
/// v1 profiles read as canonical-only; garbage reports found=false.
/// </summary>
public sealed class VoiceIdentityDiagnosticsTests
{
    [Fact]
    public void V1_profile_reports_canonical_only_with_null_drift()
    {
        var stored = """{"embedding":[0.5,0.5,0.5,0.5],"dim":4,"model":"campplus","enrolledAt":"2026-10-07T00:00:00Z"}""";
        var report = VoiceIdentityDiagnostics.Build(Guid.NewGuid(), stored, true);
        Assert.True(report.Found);
        Assert.Equal("campplus", report.Model);
        Assert.Equal(1, report.CanonicalCount);
        Assert.Equal(0, report.AdaptiveCount);
        Assert.Empty(report.Adaptive);
        Assert.Null(report.DriftCosine);
        Assert.True(report.AdaptationEnabled);
    }

    [Fact]
    public void V2_with_adaptive_reports_counts_and_bounded_drift()
    {
        var profile = new SpeakerProfile([[0.8, 0.6, 0.0, 0.0], [0.6, 0.8, 0.0, 0.0]])
            .AddAdaptive([0.0, 0.0, 1.0, 0.0], DateTimeOffset.UtcNow);
        var report = VoiceIdentityDiagnostics.Build(Guid.NewGuid(), profile.ToJson(), true);
        Assert.True(report.Found);
        Assert.Equal(2, report.CanonicalCount);
        Assert.Equal(1, report.AdaptiveCount);
        var sample = Assert.Single(report.Adaptive);
        Assert.Equal(1.0, sample.Weight);
        Assert.True(sample.AgeDays >= 0.0);
        Assert.NotNull(report.DriftCosine);
        Assert.InRange(report.DriftCosine.Value, -1.0, 1.0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"unrelated":true}""")]
    public void Garbage_reports_not_found(string? stored)
    {
        var report = VoiceIdentityDiagnostics.Build(Guid.NewGuid(), stored, false);
        Assert.False(report.Found);
        Assert.Equal(0, report.CanonicalCount);
        Assert.Equal(0, report.AdaptiveCount);
        Assert.Empty(report.Adaptive);
        Assert.Null(report.DriftCosine);
        Assert.False(report.AdaptationEnabled);
    }

    [Fact]
    public void Thresholds_match_calibrated_constants()
    {
        var report = VoiceIdentityDiagnostics.Build(Guid.NewGuid(), null, true);
        Assert.Equal(SpeakerProfile.AcceptTier, report.Thresholds.AcceptTier);
        Assert.Equal(SpeakerProfile.AdaptiveTier, report.Thresholds.AdaptiveTier);
        Assert.Equal(SpeakerProfile.MinAdaptiveQualitySpeechMs, report.Thresholds.MinSpeechMs);
        Assert.Equal(SpeakerProfile.AdaptiveSampleCap, report.Thresholds.Cap);
        Assert.Equal(SpeakerProfile.AdaptiveSampleTtl.TotalDays, report.Thresholds.TtlDays);
    }
}
