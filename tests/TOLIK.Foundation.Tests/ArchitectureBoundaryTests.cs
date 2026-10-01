using TOLIK.Agent;
using TOLIK.Contracts;
using TOLIK.Core;

namespace TOLIK.Foundation.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void Core_is_the_authorization_authority_and_does_not_execute_operating_system_commands()
    {
        var runtime = CoreRuntime.Describe();

        Assert.Equal("TOLIK.Core", runtime.ComponentName);
        Assert.True(runtime.IsAuthorizationAuthority);
        Assert.False(runtime.ExecutesOperatingSystemCommands);
    }

    [Fact]
    public void Unenrolled_agent_starts_with_all_tools_disabled()
    {
        var agent = AgentRuntime.CreateUnenrolled();

        Assert.False(agent.IsEnrolled);
        Assert.False(agent.ToolsEnabled);
        Assert.Contains("toolsEnabled=false", agent.StartupMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Core_source_does_not_reference_windows_execution_apis()
    {
        var forbiddenTokens = new[] { "Process.Start", "System.Management", "Microsoft.Win32" };
        var coreSourceDirectory = Path.Combine(FindRepositoryRoot(), "src", "TOLIK.Core");

        var matches = Directory.EnumerateFiles(coreSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => forbiddenTokens
                .Where(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetFileName(path)}: {token}"))
            .ToArray();

        Assert.Empty(matches);
    }

    [Fact]
    public void Agent_references_only_the_transport_neutral_contract_boundary()
    {
        var references = typeof(AgentRuntime).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.Contains(typeof(ProtocolBoundary).Assembly.GetName().Name, references);
        Assert.DoesNotContain(typeof(CoreRuntime).Assembly.GetName().Name, references);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TOLIK.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the TOLIK repository root.");
    }
}
