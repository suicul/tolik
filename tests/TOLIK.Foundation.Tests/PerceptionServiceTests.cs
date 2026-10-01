using TOLIK.Contracts;
using TOLIK.Core;

namespace TOLIK.Foundation.Tests;

public sealed class PerceptionServiceTests
{
    [Fact]
    public void Apply_updates_structured_world_state()
    {
        var entity = new PerceptionEntity(Guid.NewGuid(), WorldEntityKind.Room, "Office", .8);
        var state = new InMemoryPerceptionService().Apply(new PerceptionObservation("test", DateTimeOffset.UtcNow, [entity]));
        Assert.Single(state.Entities); Assert.Equal("Office", state.Entities[0].Name);
    }
}
