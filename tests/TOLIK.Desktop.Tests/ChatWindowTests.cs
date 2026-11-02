using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TOLIK.Desktop;
using TOLIK.Desktop.Bridge;
using TOLIK.Desktop.Chat;
using Xunit;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// CE-5: the conversation window built on a REAL STA dispatcher. It is created
/// offscreen and non-activated, so the owner's desktop is never disturbed — the
/// same pattern the GUI-automation tests already use.
///
/// The contract: the window renders session state, a turn disables the composer
/// (no double send), a failure renders as a notice and NOT as an answer, and the
/// link label never claims a connection that is not there.
/// </summary>
public sealed class ChatWindowTests
{
    private sealed class ScriptedTransport : IChatTransport
    {
        private readonly Queue<Func<ChatOutcome>> _script = new();
        public ScriptedTransport Then(Func<ChatOutcome> step) { _script.Enqueue(step); return this; }
        public Task<ChatOutcome> SendAsync(string message, CancellationToken cancellationToken)
            => Task.FromResult(_script.Count > 0 ? _script.Dequeue()() : ChatOutcome.Failed(ChatStatus.Unavailable, "нет сценария"));
    }

    /// <summary>Runs a body on a dedicated STA thread with a live dispatcher.</summary>
    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body failed: {failure}");
        }
    }

    /// <summary>
    /// Runs everything already queued on the window's dispatcher. The window's
    /// render calls arrive via <c>Dispatcher.BeginInvoke</c> from a worker thread,
    /// so reading a control without pumping would race the queue.
    /// </summary>
    private static void Pump(Window window)
    {
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Pumps the dispatcher until the predicate holds (bounded).</summary>
    private static void PumpUntil(Func<bool> predicate, Window window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            Pump(window);
            if (predicate())
            {
                return;
            }
            Thread.Sleep(10);
        }
        Pump(window);
        Assert.True(predicate(), "condition not reached before the deadline");
    }

    private static ChatWindow CreateWindow(ChatSession session)
    {
        var window = new ChatWindow(session)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
        };
        window.Show();
        return window;
    }

    [Fact]
    public void Window_opens_with_an_empty_transcript_and_an_honest_link_label()
    {
        OnSta(() =>
        {
            var session = new ChatSession(new ScriptedTransport());
            ChatWindow window = CreateWindow(session);
            try
            {
                window.SetLinkLabel("no Core connection");
                Assert.Equal("no Core connection", window.LinkLabel.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Successful_turn_renders_owner_line_and_answer()
    {
        OnSta(() =>
        {
            var transport = new ScriptedTransport().Then(() => ChatOutcome.Ok("Четыре."));
            var session = new ChatSession(transport);
            ChatWindow window = CreateWindow(session);
            try
            {
                window.Input.Text = "два плюс два";
                window.SendForTest();
                PumpUntil(() => session.Snapshot().Messages.Count == 2, window);

                var items = Assert.IsAssignableFrom<IEnumerable<ChatMessage>>(window.Transcript.ItemsSource).ToList();
                Assert.Equal(2, items.Count);
                Assert.Equal(ChatRole.Owner, items[0].Role);
                Assert.Equal(ChatRole.Tolik, items[1].Role);
                Assert.Equal("Четыре.", items[1].Text);
                Assert.Equal(string.Empty, window.Input.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Failure_renders_a_notice_and_leaves_no_answer()
    {
        OnSta(() =>
        {
            var transport = new ScriptedTransport()
                .Then(() => ChatOutcome.Failed(ChatStatus.Unavailable, "Core недоступен"));
            var session = new ChatSession(transport);
            ChatWindow window = CreateWindow(session);
            try
            {
                window.Input.Text = "привет";
                window.SendForTest();
                PumpUntil(() => session.Snapshot().Messages.Count == 2, window);

                var items = Assert.IsAssignableFrom<IEnumerable<ChatMessage>>(window.Transcript.ItemsSource).ToList();
                Assert.Equal(ChatRole.Notice, items[^1].Role);
                Assert.DoesNotContain(items, m => m.Role == ChatRole.Tolik);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Composer_is_disabled_while_a_turn_is_in_flight()
    {
        OnSta(() =>
        {
            var gate = new TaskCompletionSource<ChatOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            var session = new ChatSession(new BlockingTransport(gate.Task));
            ChatWindow window = CreateWindow(session);
            try
            {
                window.Input.Text = "привет";
                // Entering the busy state and rendering it are not the same instant: the
                // session raises Changed before SendAsync awaits, but the window marshals
                // the render through the dispatcher. Wait for the RENDERED facts, not for
                // the session flag, otherwise this races the queue.
                window.SendForTest();
                PumpUntil(() => !window.SendButtonControl.IsEnabled, window);

                Assert.False(window.Input.IsEnabled);
                Assert.NotEqual(string.Empty, window.StatusLabel.Text);
                Assert.True(session.IsSending);

                gate.SetResult(ChatOutcome.Ok("готово"));
                PumpUntil(() => window.SendButtonControl.IsEnabled, window);
                Assert.True(window.Input.IsEnabled);
                Assert.Equal(string.Empty, window.StatusLabel.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Note_from_a_voice_turn_lands_in_the_transcript()
    {
        OnSta(() =>
        {
            var session = new ChatSession(new ScriptedTransport());
            ChatWindow window = CreateWindow(session);
            try
            {
                window.Note("голосовой ввод недоступен");
                PumpUntil(() => session.Snapshot().Messages.Count == 1, window);

                var items = Assert.IsAssignableFrom<IEnumerable<ChatMessage>>(window.Transcript.ItemsSource).ToList();
                Assert.Equal(ChatRole.Notice, items[0].Role);
                Assert.Equal("голосовой ввод недоступен", items[0].Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Transport that holds the turn open until released.</summary>
    private sealed class BlockingTransport : IChatTransport
    {
        private readonly Task<ChatOutcome> _pending;
        public BlockingTransport(Task<ChatOutcome> pending) => _pending = pending;
        public Task<ChatOutcome> SendAsync(string message, CancellationToken cancellationToken) => _pending;
    }
}
