using TOLIK.Core;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Global adaptive-learning toggle (docs/voice-identity.md §9, VI-5): default ON,
/// owner-flippable, safe under concurrent flips (the turn path and the adaptation
/// service both read it on every candidate).
/// </summary>
public sealed class VoiceAdaptationPolicyTests
{
    [Fact]
    public void Default_is_enabled()
    {
        Assert.True(new VoiceAdaptationPolicy().Enabled);
    }

    [Fact]
    public void Flip_roundtrips()
    {
        var policy = new VoiceAdaptationPolicy();
        policy.SetEnabled(false);
        Assert.False(policy.Enabled);
        policy.SetEnabled(true);
        Assert.True(policy.Enabled);
    }

    [Fact]
    public void Parallel_sets_are_safe()
    {
        var policy = new VoiceAdaptationPolicy();
        Parallel.For(0, 10_000, i => policy.SetEnabled(i % 2 == 0));
        _ = policy.Enabled;
    }
}
