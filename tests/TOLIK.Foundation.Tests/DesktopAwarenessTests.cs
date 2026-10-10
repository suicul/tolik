using TOLIK.Contracts;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Computer Use pre-Genesis foundation (docs/computer-use.md CU-1/CU-2): the
/// CaptureScreen capability descriptor (privacy-sensitive: non-interactive captures
/// escalate to Notify — never silent), the deterministic PC State builder (Desktop
/// Awareness — applications/focused derived, never guessed), and the semantic UI
/// observation contract (targets are primary, coordinates are detail; provider-
/// replaceable, never hard-wired to one vision provider).
/// </summary>
public sealed class CaptureScreenCapabilityTests
{
    [Fact]
    public void CaptureScreen_has_descriptor_and_registry_covers_all_values()
    {
        var descriptor = CapabilityRegistry.Find(AgentCapability.CaptureScreen);
        Assert.NotNull(descriptor);
        Assert.Equal("Perception", descriptor.Category);
        Assert.Equal(ToolRiskLevel.LowRisk, descriptor.DeclaredRisk);
        Assert.True(descriptor.AllowsBackground); // periodic observation
        Assert.False(descriptor.Reversible); // captured bytes cannot be un-captured
        Assert.True(descriptor.AuditAlways);
        // The registry invariant: every AgentCapability value has exactly one descriptor.
        var all = Enum.GetValues<AgentCapability>().Cast<AgentCapability>().ToList();
        Assert.Equal(all.Count, CapabilityRegistry.All.Length);
        Assert.Equal(all.Count, CapabilityRegistry.All.Select(d => d.Id).Distinct().Count());
    }

    [Fact]
    public void Unknown_capability_fails_closed()
    {
        Assert.Null(CapabilityRegistry.Find((AgentCapability)999));
    }
}

public sealed class PcStateBuilderTests
{
    private static PcWindowState Window(string id, string process, bool focused = false, bool minimized = false) =>
        new(id, $"title {id}", process, focused, minimized, MonitorIndex: 0, new UiBounds(0, 0, 100, 100));

    [Fact]
    public void Aggregates_applications_and_focus_deterministically()
    {
        var builder = new PcStateBuilder();
        var state = builder.Apply([Window("w1", "chrome"), Window("w2", "chrome", focused: true), Window("w3", "devenv")], monitorCount: 2);
        Assert.Equal(3, state.Windows.Count);
        Assert.Equal(2, state.Applications.Count); // grouped by process
        Assert.Equal("w2", state.FocusedWindowId); // derived, never guessed
        Assert.Equal("w2", state.FocusedWindow!.WindowId);
        Assert.Equal(2, state.MonitorCount);
        var chrome = state.Applications.Single(a => a.ProcessName == "chrome");
        Assert.True(chrome.Running);
        Assert.Equal(2, chrome.WindowIds.Count);
        Assert.Equal(0, chrome.NotificationCount);
    }

    [Fact]
    public void No_focused_window_is_valid()
    {
        var builder = new PcStateBuilder();
        var state = builder.Apply([Window("w1", "chrome")], monitorCount: 1);
        Assert.Null(state.FocusedWindowId);
        Assert.Null(state.FocusedWindow);
    }

    [Fact]
    public void Notifications_update_per_application()
    {
        var builder = new PcStateBuilder();
        builder.Apply([Window("w1", "chrome")], monitorCount: 1);
        builder.SetNotifications("chrome", 3);
        Assert.Equal(3, builder.Current.Applications.Single(a => a.ProcessName == "chrome").NotificationCount);
        builder.SetNotifications("devenv", 0); // unknown app adds nothing existing
        Assert.Equal(3, builder.Current.Applications.Single(a => a.ProcessName == "chrome").NotificationCount);
    }

    [Fact]
    public void Invalid_inputs_fail_closed()
    {
        var builder = new PcStateBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.Apply(null!, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Apply([], -1));
        Assert.Throws<ArgumentException>(() => builder.SetNotifications("", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.SetNotifications("chrome", -1));
    }
}

public sealed class UiObservationTests
{
    [Fact]
    public void Semantic_targets_are_primary()
    {
        // {text, role, automation_id, bounds} better than click(812,731) (CU-2 §3).
        var button = new UiElement("Отправить", UiElementRole.Button, "send-button", new UiBounds(812, 731, 60, 30));
        Assert.True(button.IsValid);
        Assert.Equal("Отправить", button.Text);
        Assert.Equal(UiElementRole.Button, button.Role);
        Assert.Equal("send-button", button.AutomationId);
        Assert.Equal(UiElementState.Enabled, button.State);
    }

    [Fact]
    public void Representation_validity()
    {
        var valid = new UiRepresentation("w1", DateTimeOffset.UtcNow,
            [new UiElement("OK", UiElementRole.Button, null, null, UiElementState.Selected)]);
        Assert.True(valid.IsValid);
        var invalidElement = new UiRepresentation("w1", DateTimeOffset.UtcNow, [new UiElement(null, "", null, null)]);
        Assert.False(invalidElement.IsValid); // role is required
        var invalidWindow = new UiRepresentation("", DateTimeOffset.UtcNow, []);
        Assert.False(invalidWindow.IsValid);
    }

    [Fact]
    public void Element_states_are_fixed_literals()
    {
        Assert.Equal("disabled", UiElementState.Disabled);
        Assert.Equal("loading", UiElementState.Loading);
        Assert.Equal("error", UiElementState.Error);
        Assert.Equal("dialog", UiElementRole.Dialog);
    }
}
