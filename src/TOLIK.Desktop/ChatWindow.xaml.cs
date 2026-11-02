using System.Windows;
using System.Windows.Input;
using TOLIK.Desktop.Chat;
using TOLIK.Desktop.Voice;

// WinForms is referenced project-wide (tray icon) and also defines KeyEventArgs.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

// CE-5: the chat window's send and voice paths are covered on a real STA dispatcher.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TOLIK.Desktop.Tests")]

namespace TOLIK.Desktop;

/// <summary>
/// The conversation surface of the main client (owner decision 03.11.2026: Windows
/// is the target platform, so it must carry the primary interaction, not only the
/// cephalon). Deliberately thin: it renders <see cref="ChatSession"/> state and
/// forwards typed or spoken input — no authorization, no memory, no retry policy.
///
/// Voice (owner decision: the microphone is the PC's): press the mic, speak, press
/// again. The audio goes to Core, which does STT, speaker identification, memory and
/// TTS; this window only records and plays. What the owner said is shown as a note
/// only after Core reports what it heard — the client never guesses.
///
/// The window is a normal, activatable, taskbar window on purpose: a chat needs a
/// keyboard focus. The cephalon overlay keeps its click-through posture separately.
/// </summary>
public partial class ChatWindow : Window
{
    private readonly ChatSession _session;
    private readonly VoiceTurnClient? _voice;
    private readonly MicrophoneRecorder? _recorder;
    private CancellationTokenSource? _inFlight;
    private CancellationTokenSource? _voiceCts;
    private bool _recording;

    public ChatWindow(ChatSession session, VoiceTurnClient? voice = null, MicrophoneRecorder? recorder = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _voice = voice;
        _recorder = recorder;
        InitializeComponent();
        _session.Changed += OnSessionChanged;

        if (_recorder is not null)
        {
            _recorder.LevelChanged += OnLevelChanged;
        }
        if (_voice is null || _recorder is null)
        {
            MicButton.IsEnabled = false;
            MicButton.ToolTip = "голосовой ввод недоступен";
        }

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
    /// covered on a real dispatcher without synthesising keyboard input.
    /// </summary>
    internal void SendForTest() => _ = SendCurrentAsync();

    /// <summary>Drives the microphone toggle as the mic button does.</summary>
    internal void ToggleMicForTest() => ToggleMicrophone();

    protected override void OnClosed(EventArgs e)
    {
        _session.Changed -= OnSessionChanged;
        if (_recorder is not null)
        {
            _recorder.LevelChanged -= OnLevelChanged;
        }
        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _inFlight = null;
        _voiceCts?.Cancel();
        _voiceCts?.Dispose();
        _voiceCts = null;
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

    private void OnMicClick(object sender, RoutedEventArgs e) => ToggleMicrophone();

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

    /// <summary>One press starts recording, the next press ends it and sends the turn.</summary>
    private void ToggleMicrophone()
    {
        if (_voice is null || _recorder is null)
        {
            return;
        }

        if (!_recording)
        {
            // Barge-in: the owner talking stops TOLIK mid-sentence.
            _recorder.StopPlayback();
            if (!_recorder.Start(out string? error))
            {
                _session.Note($"микрофон: {error}");
                return;
            }
            _recording = true;
            MicButton.Content = "■";
            StatusLabel.Text = "слушаю…";
            return;
        }

        _recording = false;
        MicButton.Content = "🎤";
        StatusLabel.Text = string.Empty;
        byte[] audio = _recorder.Stop();
        if (audio.Length == 0)
        {
            _session.Note("запись пустая — микрофон ничего не передал");
            return;
        }

        _ = SendVoiceAsync(audio);
    }

    private async Task SendVoiceAsync(byte[] audio)
    {
        if (_voice is null || _recorder is null)
        {
            return;
        }

        _voiceCts?.Dispose();
        _voiceCts = new CancellationTokenSource();
        CancellationToken token = _voiceCts.Token;

        StatusLabel.Text = "распознаю…";
        SendButtonControl.IsEnabled = false;
        try
        {
            VoiceTurnResult result = await _voice.SendAsync(
                audio,
                evt =>
                {
                    // Audio chunks arrive on the read loop; the recorder serializes
                    // playback so sentence chunks never overlap.
                    if (evt is VoiceStreamEvent.Audio chunk)
                    {
                        _recorder.Play(chunk.Data);
                    }
                },
                token);

            if (result.IsSuccess)
            {
                if (result.Status == VoiceTurnStatus.Silent)
                {
                    _session.Note("не расслышал — попробуйте ещё раз");
                }
                else
                {
                    _session.Note($"голос: {result.Transcript}");
                }
            }
            else
            {
                _session.Note(DescribeVoiceFailure(result));
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed or a new turn started; nothing to report.
        }
        finally
        {
            StatusLabel.Text = string.Empty;
            SendButtonControl.IsEnabled = !_session.IsSending;
        }
    }

    /// <summary>Token-free, actionable reason — the same honesty rule as the chat path.</summary>
    internal static string DescribeVoiceFailure(VoiceTurnResult result) => result.Status switch
    {
        VoiceTurnStatus.Unavailable => $"голосовой контур недоступен — {result.Detail ?? "нет связи"}",
        VoiceTurnStatus.AuthRejected => "сессия отклонена, нужен новый вход",
        VoiceTurnStatus.Rejected => result.Detail ?? "Core отклонил голосовой запрос",
        VoiceTurnStatus.Malformed => $"некорректный поток — {result.Detail ?? "неизвестно"}",
        _ => "не удалось отправить голос",
    };

    private void OnLevelChanged(double level)
    {
        // Called from the capture callback thread; keep the UI honest but never block it.
        if (!_recording)
        {
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (_recording)
            {
                StatusLabel.Text = level > 0.02 ? "слушаю…" : "слушаю… (тихо)";
            }
        });
    }
}
