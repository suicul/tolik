using TOLIK.Core;

namespace TOLIK.Foundation.Tests;

public sealed class MemoryTests
{
    [Fact]
    public void Private_memory_isolated_while_shared_memory_retrieves()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var memory = new ScopedMemory();
        memory.Add(a, MemoryScope.Private, "secret preference", "user-feedback", .9);
        memory.Add(a, MemoryScope.Shared, "family joke", "user-feedback", .8);
        Assert.Empty(memory.Search(b, "secret"));
        Assert.Single(memory.Search(b, "family"));
    }

    [Fact]
    public void Owner_can_delete_own_memory_in_every_scope()
    {
        var owner = Guid.NewGuid(); var memory = new ScopedMemory();
        foreach (var scope in new[] { MemoryScope.Private, MemoryScope.Shared, MemoryScope.Public })
        {
            var item = memory.Add(owner, scope, $"fact {scope}", "user", 0.9);
            Assert.True(memory.Remove(owner, item.Id));
            Assert.Empty(memory.Search(owner, $"fact {scope}"));
        }
    }

    [Fact]
    public void Stranger_cannot_delete_and_unknown_id_returns_false()
    {
        var owner = Guid.NewGuid(); var stranger = Guid.NewGuid(); var memory = new ScopedMemory();
        var item = memory.Add(owner, MemoryScope.Shared, "family joke", "user", 0.8);
        Assert.False(memory.Remove(stranger, item.Id));
        Assert.Single(memory.Search(owner, "family joke"));
        Assert.False(memory.Remove(owner, Guid.NewGuid()));
    }
}
