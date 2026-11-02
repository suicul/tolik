using System.Windows;
using System.Windows.Input;
using TOLIK.Desktop.Chat;

// WinForms is referenced project-wide (tray icon) and also defines KeyEventArgs.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

// CE-5: the chat window's send path is covered by tests on a real STA dispatcher.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TOLIK.Desktop.Tests")]

namespace TOLIK.Desktop;

/// <summary>
/// The conversation surface of the main client (owner decision 03.11.2026: Windows
/// is the target platform, so it must carry the primary interaction, not only the
/// cephalon). Deliberately thin: it renders <see cref="ChatSession"/> state and
/// forwards typed input — no authorization, no memory, no retry policy of its own.
///
/// The window is a normal, activatable, taskbar window on purpose: a chat needs a
/// keyboard focus. The cephalon overlay keeps its click-through posture separately.
/// </summary>
public partial class ChatWindow : Window
{
    private readonly ChatSession _session;
    private CancellationTokenSource? _inFlight;

    public ChatWindow(ChatSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        InitializeComponent();
        _session.Changed += OnSessionChanged;
        Render(_session.Snapshot());
        Loaded += (_, _) => Input.Focus();
    }

    /// <summary>Human label for the Core link, set by the app when the link changes.</summary>
    public void SetLinkLabel(string label)
    {
        LinkLabel.Text = label;
        LinkLabel.Foreground = label.StartsWith("Core:", StringComparison.Ordinal)
            ? (System.Windows.Media.Brush)FindResource("AccentBrush")
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
    }

    /// <summary>Mirrors a voice turn or any other local event into the transcript.</summary>
    public void Note(string text) => _session.Note(text);

    /// <summary>
    /// Drives a send as the Send button does. Exists so the window behaviour is
    /// covered on a real dispatcher without synthesising input; the button and the
    /// Enter key both call the same path.
    /// </summary>
    internal void SendForTest()
    {
        SendCurrentAsync().GetAwaiter();
    }

    protected override void OnClosed(EventArgs e)
    {
        _session.Changed -= OnSessionChanged;
        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _inFlight = null;
        base.OnClosed(e);
    }

    private void OnSessionChanged(ChatViewState state)
    {
        // Session events arrive on whatever thread ran the turn; the renderer is WPF.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Render(state));
            return;
        }
        Render(state);
    }

    private void Render(ChatViewState state)
    {
        Transcript.ItemsSource = state.Messages;
        if (state.Messages.Count > 0)
        {
            TranscriptScroll.ScrollToEnd();
        }

        StatusLabel.Text = state.StatusLine ?? string.Empty;
        SendButtonControl.IsEnabled = !state.IsSending;
        Input.IsEnabled = !state.IsSending;
        if (!state.IsSending)
        {
            Input.Focus();
        }
    }

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendCurrentAsync();

    private async void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SendCurrentAsync();
        }
    }

    private async Task SendCurrentAsync()
    {
        string text = Input.Text;
        if (string.IsNullOrWhiteSpace(text) || _session.IsSending)
        {
            return;
        }

        Input.Clear();
        _inFlight?.Dispose();
        _inFlight = new CancellationTokenSource();
        try
        {
            await _session.SendAsync(text, _inFlight.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by closing the window or a new turn — the session already
            // recorded the notice.
        }
    }
}
