using TOLIK.Contracts;

namespace TOLIK.Foundation.Tests;

public sealed class PerceptionTests
{
    [Fact]
    public void Valid_observation_accepts_structured_entities()
    {
        var entity = new PerceptionEntity(Guid.NewGuid(), WorldEntityKind.Application, "Terminal", .9);
        var observation = new PerceptionObservation("cloud-vision", DateTimeOffset.UtcNow, [entity]);
        Assert.True(observation.IsValid);
        Assert.True(entity.IsValid);
    }

    [Fact]
    public void Invalid_confidence_rejects_observation()
    {
        var entity = new PerceptionEntity(Guid.NewGuid(), WorldEntityKind.Object, "unknown", 1.1);
        var observation = new PerceptionObservation("cloud-vision", DateTimeOffset.UtcNow, [entity]);
        Assert.False(observation.IsValid);
    }
}
