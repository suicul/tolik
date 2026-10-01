using TOLIK.Agent;

namespace TOLIK.Foundation.Tests;

/// <summary>Agent-side path isolation (AgentPathGuard). The parent link-walk must
/// survive volume roots: DirectoryInfo("C:\").ResolveLinkTarget throws
/// DirectoryNotFound, which used to break every absolute path.</summary>
public sealed class AgentPathTests
{
    [Fact]
    public void Resolves_allowed_root_and_descendant()
    {
        var root = Path.Combine(Path.GetTempPath(), "tolik-agent-root");
        Directory.CreateDirectory(root);
        var child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);
        try
        {
            Assert.Equal(Path.GetFullPath(root), AgentPathGuard.ResolveAllowedPath(root, [root]));
            Assert.Equal(Path.GetFullPath(child), AgentPathGuard.ResolveAllowedPath(child, [root]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Rejects_outside_paths_and_missing_roots()
    {
        var root = Path.Combine(Path.GetTempPath(), "tolik-agent-root");
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
                AgentPathGuard.ResolveAllowedPath(Path.GetTempPath(), [root]));
            Assert.Throws<UnauthorizedAccessException>(() =>
                AgentPathGuard.ResolveAllowedPath(root, []));
            Assert.Throws<ArgumentException>(() =>
                AgentPathGuard.ResolveAllowedPath("  ", [root]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
