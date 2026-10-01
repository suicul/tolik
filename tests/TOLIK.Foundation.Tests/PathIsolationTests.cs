using TOLIK.Infrastructure.Authorization;

namespace TOLIK.Foundation.Tests;

public sealed class PathIsolationTests
{
    [Fact]
    public void Allows_only_descendants_of_configured_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "tolik-root");
        var policy = new PathIsolationPolicy([root]);
        Assert.True(policy.IsAllowed(Path.Combine(root, "project", "file.cs")));
        Assert.False(policy.IsAllowed(Path.Combine(root, "..", "outside.txt")));
        Assert.False(policy.IsAllowed(Path.Combine(root, ".env")));
    }
}
