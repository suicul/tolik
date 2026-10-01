using TOLIK.Core;

namespace TOLIK.Foundation.Tests;

public sealed class PersonalContextTests
{
    [Fact]
    public void Private_humor_isolated_and_shared_humor_visible()
    {
        var owner = Guid.NewGuid(); var other = Guid.NewGuid(); var context = new PersonalContext();
        context.AddHumor(owner, MemoryScope.Private, "inside joke", "family");
        context.AddHumor(owner, MemoryScope.Shared, "shared meme", "family");
        Assert.Single(context.GetHumor(other));
        Assert.Equal(2, context.GetHumor(owner).Count);
    }
}
