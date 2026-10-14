using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using TOLIK.Agent;
using Xunit;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// CU-6 GUI actions v1 — the provider driven against a REAL WPF window via REAL UIA
/// (an offscreen STA-hosted window: ShowActivated=false, ShowInTaskbar=false, far
/// offscreen — the user's desktop is never disturbed). The contract: semantic
/// addressing works end-to-end (automation_id → element), GuiRead reports the honest
/// state (the TextBlock's UIA Name follows its text), GuiClick invokes through the
/// InvokePattern (the click handler flips the text — the follow-up read SEES the
/// change), and everything malformed/missing/uninvocable fails closed with an honest
/// error — never a guess.
/// </summary>
public sealed class GuiAutomationProviderTests
{
    private const string TextId = "tolikCua6TestText";
    private const string ButtonId = "tolikCua6TestButton";
    private const string PlainId = "tolikCua6TestPlain";

    /// <summary>An offscreen WPF window hosted on a dedicated STA thread with a live
    /// dispatcher (UIA pattern calls marshal into it). Fully offscreen and
    /// non-activating — never disturbs the real desktop.</summary>
    private sealed class TestWindowHost : IDisposable
    {
        private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Window Window = null!; // assigned on the STA thread (a field — the ctor-only property rule doesn't apply)
        public Thread Thread { get; } = null!;

        public TestWindowHost()
        {
            var thread = new Thread(() =>
            {
                var text = new TextBlock { Text = "before" };
                AutomationProperties.SetAutomationId(text, TextId);
                var button = new Button { Content = "Click me" };
                AutomationProperties.SetAutomationId(button, ButtonId);
                button.Click += (_, _) => text.Text = "after";
                var plain = new TextBlock { Text = "plain" };
                AutomationProperties.SetAutomationId(plain, PlainId);
                var stack = new StackPanel();
                stack.Children.Add(text);
                stack.Children.Add(button);
                stack.Children.Add(plain);
                var window = new Window
                {
                    Content = stack,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -30000,
                    Top = -30000,
                    Width = 220,
                    Height = 160,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Title = "tolik-cua6-test"
                };
                window.Loaded += (_, _) => _loaded.TrySetResult();
                Window = window;
                window.Show();
                Dispatcher.Run(); // pump until BeginInvokeShutdown
            });
            thread.SetApartmentState(ApartmentState.STA);
            Thread = thread;
            thread.Start();
        }

        public Task Loaded => _loaded.Task;

        public void Dispose()
        {
            try
            {
                Window.Dispatcher.Invoke(() => Window.Close());
                Window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            }
            catch
            {
                // the window thread may already be gone — cleanup is best-effort
            }

            Thread.Join(3000);
        }
    }

    private static async Task<string> ReadUntilAsync(string spec, string contains, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var result = GuiAutomationProvider.Read(spec);
            if (result.Contains(contains, StringComparison.Ordinal)) return result;
            if (DateTime.UtcNow > deadline) return result; // let the assertion fail with the actual content
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Read_sees_the_honest_element_state_by_automation_id()
    {
        using var host = new TestWindowHost();
        var loaded = await Task.WhenAny(host.Loaded, Task.Delay(5000));
        Assert.True(loaded == host.Loaded, "the test window must load within the timeout");

        var result = await ReadUntilAsync($"automation_id={TextId}", "name=before");
        Assert.Contains("name=before", result, StringComparison.Ordinal);
        Assert.Contains("controlType=ControlType.Text", result, StringComparison.Ordinal);
        Assert.Contains("isEnabled=true", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Click_invokes_the_button_and_the_follow_up_read_sees_the_change()
    {
        using var host = new TestWindowHost();
        var loaded = await Task.WhenAny(host.Loaded, Task.Delay(5000));
        Assert.True(loaded == host.Loaded, "the test window must load within the timeout");

        var invoked = GuiAutomationProvider.Click($"automation_id={ButtonId}");
        Assert.Contains("invoked=", invoked, StringComparison.Ordinal);

        // The click handler runs on the window's dispatcher — the follow-up read
        // (ClosedLoopRunner parity) must SEE the semantic state change.
        var after = await ReadUntilAsync($"automation_id={TextId}", "name=after");
        Assert.Contains("name=after", after, StringComparison.Ordinal);
        Assert.DoesNotContain("name=before", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Click_on_an_uninvocable_element_fails_honestly()
    {
        using var host = new TestWindowHost();
        var loaded = await Task.WhenAny(host.Loaded, Task.Delay(5000));
        Assert.True(loaded == host.Loaded, "the test window must load within the timeout");

        // A plain TextBlock exposes no Invoke/Toggle/Select — the provider refuses
        // to guess instead of synthesizing a mouse event.
        var ex = Assert.Throws<InvalidOperationException>(
            () => GuiAutomationProvider.Click($"automation_id={PlainId}"));
        Assert.Contains("no invocation pattern", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_spec_fails_closed_before_any_UIA_work()
    {
        var ex = Assert.Throws<UnauthorizedAccessException>(
            () => GuiAutomationProvider.Read("coordinate=100,200"));
        Assert.Contains("malformed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_element_is_an_honest_failure()
    {
        using var host = new TestWindowHost();
        var loaded = await Task.WhenAny(host.Loaded, Task.Delay(5000));
        Assert.True(loaded == host.Loaded, "the test window must load within the timeout");

        var ex = Assert.Throws<InvalidOperationException>(
            () => GuiAutomationProvider.Read("automation_id=tolikCua6NoSuchElement"));
        Assert.Contains("not found", ex.Message, StringComparison.Ordinal);
    }
}
