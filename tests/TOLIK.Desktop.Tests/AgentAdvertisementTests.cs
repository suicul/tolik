using TOLIK.Agent;
using TOLIK.Contracts;
using Xunit;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// The agent's registration advertisement (CU-6 live-loop lesson): the advertised
/// capability list MUST stay in sync with what the agent can actually execute — a
/// capability the agent supports but does not advertise is DEAD (the Core dispatcher
/// refuses to send it: "No connected agent advertises the requested capability").
/// The invariant: every advertised name is a real AgentCapability enum value (no
/// typos — the Core parses the names fail-closed), and the GUI-action pair is present
/// (the dispatch switch handles them; the advertisement was the missing half).
/// </summary>
public sealed class AgentAdvertisementTests
{
    [Fact]
    public void Every_advertised_name_is_a_real_capability_enum_value()
    {
        Assert.NotEmpty(WindowsAgent.AdvertisedCapabilities);
        foreach (var name in WindowsAgent.AdvertisedCapabilities)
            Assert.True(
                Enum.TryParse<AgentCapability>(name, out _),
                $"advertised capability '{name}' is not a real AgentCapability value (the Core parses names fail-closed).");
    }

    [Fact]
    public void Advertisement_has_no_duplicates()
    {
        var distinct = WindowsAgent.AdvertisedCapabilities.Distinct(StringComparer.Ordinal).Count();
        Assert.Equal(WindowsAgent.AdvertisedCapabilities.Length, distinct);
    }

    [Theory]
    [InlineData("GuiRead")]
    [InlineData("GuiClick")]
    public void Gui_action_pair_is_advertised(string name)
    {
        Assert.Contains(name, WindowsAgent.AdvertisedCapabilities, StringComparer.Ordinal);
    }
}
