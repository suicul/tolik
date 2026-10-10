using TOLIK.Desktop.Presence;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// Mode policy: the typed table must cover every mode, stay within sane ranges,
/// and encode the right behavior for the throttled modes.
/// </summary>
public class ModePolicyTests
{
    [Fact]
    public void Table_covers_every_mode()
    {
        foreach (PresenceMode mode in Enum.GetValues<PresenceMode>())
        {
            Assert.True(ModePolicyTable.Table.ContainsKey(mode), $"missing policy for {mode}");
        }
    }

    [Fact]
    public void Policies_stay_within_sane_ranges()
    {
        foreach (var (mode, policy) in ModePolicyTable.Table)
        {
            Assert.True(double.IsFinite(policy.TargetFps), $"{mode} fps not finite");
            Assert.InRange(policy.TargetFps, 5.0, 60.0);
            Assert.InRange(policy.IdleIntensity, 0.0, 1.2);
            Assert.InRange(policy.Interactivity, 0.0, 1.0);
        }
    }

    [Fact]
    public void Hidden_is_the_most_throttled_mode()
    {
        ModePolicy hidden = ModePolicyTable.GetSafe(PresenceMode.Hidden);
        double minFps = ModePolicyTable.Table.Values.Min(p => p.TargetFps);
        Assert.Equal(minFps, hidden.TargetFps);
        Assert.False(hidden.AllowParticles);
        Assert.False(hidden.AllowMotion);
        Assert.False(hidden.ShowCaption);
        Assert.True(hidden.ForcePassive);
    }

    [Fact]
    public void Gaming_and_Presentation_are_forced_passive()
    {
        Assert.True(ModePolicyTable.GetSafe(PresenceMode.Gaming).ForcePassive);
        Assert.True(ModePolicyTable.GetSafe(PresenceMode.Presentation).ForcePassive);
    }

    [Fact]
    public void Normal_mode_allows_the_full_experience()
    {
        ModePolicy normal = ModePolicyTable.GetSafe(PresenceMode.Normal);
        Assert.True(normal.AllowParticles);
        Assert.True(normal.AllowMotion);
        Assert.True(normal.ShowCaption);
        Assert.False(normal.ForcePassive);
        Assert.True(normal.TargetFps >= 30.0);
    }

    [Fact]
    public void Every_mode_maps_to_a_distinct_policy()
    {
        var modes = ModePolicyTable.AllModes.ToList();
        var distinct = modes.Select(m => ModePolicyTable.Table[m]).Distinct().ToList();
        Assert.Equal(modes.Count, distinct.Count);
    }

    [Fact]
    public void Unmapped_mode_fails_closed_to_Normal_policy()
    {
        ModePolicy fallback = ModePolicyTable.GetSafe((PresenceMode)999);
        Assert.Equal(ModePolicyTable.Table[PresenceMode.Normal], fallback);
    }
}
