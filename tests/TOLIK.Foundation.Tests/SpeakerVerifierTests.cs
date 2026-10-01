using TOLIK.Core;

namespace TOLIK.Foundation.Tests;

public sealed class SpeakerVerifierTests
{
    [Fact]
    public void Cosine_of_identical_vectors_is_one()
    {
        var v = new[] { 0.6, 0.8, 0.0 };
        Assert.Equal(1.0, SpeakerVerifier.Cosine(v, v), precision: 9);
    }

    [Fact]
    public void Cosine_of_orthogonal_vectors_is_zero()
    {
        Assert.Equal(0.0, SpeakerVerifier.Cosine(new[] { 1.0, 0.0 }, new[] { 0.0, 1.0 }), precision: 9);
    }

    [Fact]
    public void Threshold_accepts_live_calibrated_values()
    {
        // Live calibration (CampPlus): same speaker ≈0.49–1.0, different ≈0.2–0.3.
        Assert.True(SpeakerVerifier.Accept(0.80));
        Assert.True(SpeakerVerifier.Accept(0.49));
        Assert.True(SpeakerVerifier.Accept(SpeakerVerifier.AcceptThreshold));
        Assert.False(SpeakerVerifier.Accept(0.31));
        Assert.False(SpeakerVerifier.Accept(0.0));
    }

    [Fact]
    public void Cosine_rejects_invalid_inputs()
    {
        Assert.Throws<ArgumentException>(() => SpeakerVerifier.Cosine([], []));
        Assert.Throws<ArgumentException>(() => SpeakerVerifier.Cosine(new[] { 1.0 }, new[] { 1.0, 2.0 }));
        Assert.Throws<ArgumentException>(() => SpeakerVerifier.Cosine(new[] { 0.0, 0.0 }, new[] { 0.0, 0.0 }));
    }
}
