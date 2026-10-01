using TOLIK.Contracts;

namespace TOLIK.Foundation.Tests;

public sealed class HomeDeploymentTests
{
    [Fact]
    public void Home_event_has_explicit_retention_and_source()
    {
        var item = new HomePerceptionEvent(Guid.NewGuid(), "camera-1", "person-detected", RetentionClass.Event, DateTimeOffset.UtcNow);
        Assert.True(item.IsValid);
        Assert.Equal(RetentionClass.Event, item.Retention);
    }

    [Fact]
    public void Empty_source_is_invalid()
    {
        var item = new HomePerceptionEvent(Guid.NewGuid(), "", "motion", RetentionClass.Ephemeral, DateTimeOffset.UtcNow);
        Assert.False(item.IsValid);
    }
}
