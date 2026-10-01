using TOLIK.Contracts;

namespace TOLIK.Foundation.Tests;

public sealed class VoiceProfileTests
{
    [Fact]
    public void Original_russian_tactical_profile_is_valid()
    {
        var profile = new VoiceProfile("TOLIK Tactical Original", "ru-RU", -5, -.08, .72, .18, .28, VoiceMode.Tactical);
        Assert.True(profile.IsValid);
    }

    [Theory]
    [InlineData(VoiceRuntimeMode.Gaming)]
    [InlineData(VoiceRuntimeMode.GamingAssist)]
    public void Gaming_modes_disable_local_inference(VoiceRuntimeMode mode)
    {
        var policy = VoiceRuntimePolicy.For(mode);
        Assert.False(policy.LocalInferenceEnabled);
        Assert.False(policy.TtsEnabled);
    }
}
