using System.Drawing;
using System.Windows;
using TOLIK.Desktop.Bridge;
using TOLIK.Desktop.Presence;
using TOLIK.Desktop.Speech;
using WinForms = System.Windows.Forms;

// WinForms also defines Application — pin the WPF one (the XAML partial already uses it).
using Application = System.Windows.Application;
// The tray glyph is drawn with GDI+, so pin the System.Drawing Point here.
using Point = System.Drawing.Point;

namespace TOLIK.Desktop;

/// <summary>
/// Composition root for the desktop presence: overlay window + tray icon + mode
/// policy + global hotkey + the Core expression link (CE-2). Deliberately thin:
/// TOLIK is a background system (AGENTS §34) — the app contains NO identity,
/// memory, permissions or authorization logic. The link is a READ-ONLY consumer
/// of Core's expression stream: token from the dev endpoint, in-memory only,
/// never persisted (SECURITY §6); the renderer renders, it never decides.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\TOLIK.Desktop.Cephalon";
    private const string HotkeyText = "Ctrl+Alt+T";

    private Mutex? _singleInstance;
    private CephalonWindow? _window;
    private WinForms.NotifyIcon? _tray;
    private Icon? _trayIcon;

    private CoreExpressionClient? _coreLink;
    private LocalTokenSource? _linkTokens;
    private CancellationTokenSource? _linkCts;
    private CoreHealthWatcher? _healthWatcher;
    private SpeechPresenter? _speech;

    private WinForms.ToolStripMenuItem? _showItem;
    private WinForms.ToolStripMenuItem? _interactiveItem;
    private WinForms.ToolStripMenuItem? _debugItem;
    private readonly Dictionary<PresenceMode, WinForms.ToolStripMenuItem> _modeItems = new();

    private PresenceMode _mode = PresenceMode.Normal;
    private bool _modeIsAuto = true; // CE-4: the auto-set flag — the tray's manual choice pins and is never overridden
    private System.Windows.Threading.DispatcherTimer? _desktopWatcher; // CE-4: the desktop-awareness poll
    private bool _interactive;
    private bool _debugOverlay;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        _window = new CephalonWindow();
        _window.InteractiveToggleRequested += () => SetInteractive(!_interactive);
        _window.Show();

        _tray = BuildTray();

        SetMode(PresenceMode.Normal, auto: true);
        SetInteractive(false);

        StartSpeech();
        StartCoreLink();
        StartDesktopWatcher();
    }

    /// <summary>CE-4 (PLAN REFORM PRG-2b): the desktop-awareness poll — the SAFE facts
    /// only (the foreground process name + the fullscreen proxy; NO window content,
    /// NO text, NO screenshots — директива §12/§50). The auto-switching never touches a
    /// mode the owner chose manually: ModeArbitration treats the manual choice as
    /// sacred. Fullscreen GAME → Gaming (quiet, click-through); other fullscreen →
    /// Presentation; back to Normal when it ends.</summary>
    private void StartDesktopWatcher()
    {
        _desktopWatcher = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _desktopWatcher.Tick += (_, _) =>
        {
            var observation = TOLIK.Desktop.Presence.DesktopWatcher.Observe();
            var game = TOLIK.Desktop.Presence.DesktopWatcher.IsGameProcess(
                observation.ProcessName, Environment.GetEnvironmentVariable("TOLIK_GAME_PROCESSES"));
            var next = TOLIK.Desktop.Presence.ModeArbitration.Decide(_mode, _modeIsAuto, observation.Fullscreen, game);
            if (next is not null)
                SetMode(next.Value, auto: true);
        };
        _desktopWatcher.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _desktopWatcher?.Stop();
        _desktopWatcher = null;
        _linkCts?.Cancel();
        _coreLink = null;
        _linkTokens?.Dispose();
        _linkTokens = null;
        _linkCts?.Dispose();
        _linkCts = null;
        _healthWatcher?.Dispose();
        _healthWatcher = null;
        _speech = null;

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        _trayIcon?.Dispose();
        _window?.Close();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    // ---------- Core expression link (CE-2, read-only consumer) ----------

    /// <summary>
    /// Starts the Core link: dev-token source (memory only) → authenticated WS →
    /// cephalon_expression frames. Frames and state snapshots are raised on a worker
    /// thread and marshaled onto the dispatcher; failures surface as honest link
    /// states — the overlay never crashes and never lies about being connected.
    /// </summary>
    private void StartCoreLink()
    {
        if (_window is null)
        {
            return;
        }

        CoreLinkOptions options = CoreLinkOptions.FromEnvironment();
        var tokens = new LocalTokenSource(options);
        var client = new CoreExpressionClient(options, tokens, () => new ClientWebSocketSocket());
        var cts = new CancellationTokenSource();

        _coreLink = client;
        _linkTokens = tokens;
        _linkCts = cts;

        _window.CephalonHost.CoreLink.EndpointLabel = options.BaseUri.Authority;

        client.FrameReceived += frame =>
            _window?.Dispatcher.BeginInvoke(() => _window.CephalonHost.OnCoreExpressionFrame(frame));
        client.StateChanged += snapshot =>
            _window?.Dispatcher.BeginInvoke(() => OnCoreLinkStateChanged(snapshot));
        client.MessageReceived += message =>
            _window?.Dispatcher.BeginInvoke(() => _speech?.OnMessage(message));

        _ = System.Threading.Tasks.Task.Run(() => client.RunAsync(cts.Token));

        StartHealthWatcher(options);
    }

    /// <summary>
    /// Wires the mind's voice: the presenter owns the policy (bounded queue, dedupe,
    /// the emergency-stop gate), the band owns only the choreography. Every entry
    /// point runs on the dispatcher (the client events arrive on worker threads).
    /// </summary>
    private void StartSpeech()
    {
        if (_window is null)
        {
            return;
        }

        var speech = new SpeechPresenter();
        speech.SpeechStarted += message => _window.SpeechBand.Show(message);
        _window.SpeechBand.CycleCompleted += speech.NotifyCycleCompleted;
        _speech = speech;
    }

    /// <summary>
    /// Starts the emergency-stop poll: the REAL Core /health fact feeds the speech
    /// gate. While the mind is paused, proactive lines stay silent (honest silence);
    /// presence digests may still show. Transitions arrive on a worker thread and
    /// are marshaled; the poll shares the link's cancellation lifetime.
    /// </summary>
    private void StartHealthWatcher(CoreLinkOptions options)
    {
        var watcher = new CoreHealthWatcher(options);
        _healthWatcher = watcher;
        watcher.EmergencyStopChanged += stopped =>
            _window?.Dispatcher.BeginInvoke(() => _speech?.SetEmergencyStopped(stopped));
        if (_linkCts is not null)
        {
            _ = System.Threading.Tasks.Task.Run(() => watcher.RunAsync(_linkCts.Token));
        }
    }

    private void OnCoreLinkStateChanged(LinkSnapshot snapshot)
    {
        if (_window is not null)
        {
            _window.CephalonHost.OnCoreLinkState(snapshot);
        }
        UpdateTrayText();
    }

    // ---------- tray ----------

    private WinForms.NotifyIcon BuildTray()
    {
        _trayIcon = CreateTrayIcon();

        var menu = new WinForms.ContextMenuStrip();

        _showItem = new WinForms.ToolStripMenuItem("Hide overlay");
        _showItem.Click += (_, _) => ToggleOverlayVisibility();
        menu.Items.Add(_showItem);

        var modeMenu = new WinForms.ToolStripMenuItem("Mode");
        foreach (PresenceMode mode in ModePolicyTable.AllModes.OrderBy(m => m.ToString()))
        {
            var item = new WinForms.ToolStripMenuItem(mode.ToString()) { CheckOnClick = true };
            item.Click += (_, _) => SetMode(mode);
            _modeItems[mode] = item;
            modeMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(modeMenu);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        _interactiveItem = new WinForms.ToolStripMenuItem("Interactive (click && drag)")
        {
            CheckOnClick = true,
            ToolTipText = $"Toggle with {HotkeyText} or double-click the Cephalon",
        };
        _interactiveItem.Click += (_, _) => SetInteractive(_interactiveItem.Checked);
        menu.Items.Add(_interactiveItem);

        _debugItem = new WinForms.ToolStripMenuItem("Debug overlay") { CheckOnClick = true };
        _debugItem.Click += (_, _) => SetDebugOverlay(_debugItem.Checked);
        menu.Items.Add(_debugItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(exitItem);

        var tray = new WinForms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "TOLIK — starting…",
            Visible = true,
            ContextMenuStrip = menu,
        };
        tray.DoubleClick += (_, _) => ToggleOverlayVisibility();

        return tray;
    }

    /// <summary>Draws a small original Cephalon glyph (dark disc, blue octagon eye, dark pupil).</summary>
    private static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var shellBrush = new SolidBrush(Color.FromArgb(12, 19, 34));
            using var irisBrush = new SolidBrush(Color.FromArgb(88, 166, 255));
            using var pupilBrush = new SolidBrush(Color.FromArgb(4, 6, 12));
            using var glintBrush = new SolidBrush(Color.FromArgb(160, 255, 255, 255));

            g.FillEllipse(shellBrush, 1, 1, 30, 30);
            g.DrawEllipse(new Pen(Color.FromArgb(52, 80, 122)), 1, 1, 30, 30);

            var octagon = new Point[8];
            for (int i = 0; i < 8; i++)
            {
                double a = (Math.PI * 2 * i / 8.0) + (Math.PI / 8.0);
                octagon[i] = new Point(
                    16 + (int)Math.Round(11 * Math.Cos(a)),
                    16 + (int)Math.Round(11 * Math.Sin(a)));
            }
            g.FillPolygon(irisBrush, octagon);
            g.FillEllipse(pupilBrush, 10, 10, 12, 12);
            g.FillEllipse(glintBrush, 11, 11, 4, 4);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    // ---------- state changes ----------

    private void SetMode(PresenceMode mode, bool auto = false)
    {
        _mode = mode;
        _modeIsAuto = auto; // CE-4: the tray's default call (auto:false) pins the choice;
        ModePolicy policy = ModePolicyTable.GetSafe(mode); // the watcher's auto:true stays overridable

        if (_window is not null)
        {
            _window.SetInteractive(_interactive, policy);
            _window.CephalonHost.ApplyMode(mode, policy);
        }

        foreach (var item in _modeItems)
        {
            item.Value.Checked = item.Key == mode;
        }

        UpdateTrayText();
    }

    /// <summary>Tray tooltip = presence mode + the honest Core link state (≤63 chars,
    /// the NotifyIcon limit). Never claims a connection that is not there.</summary>
    private void UpdateTrayText()
    {
        if (_tray is null)
        {
            return;
        }

        string link = (_window?.CephalonHost.CoreLink) switch
        {
            { State: CoreLinkState.Connected, LastMode: { } coreMode } => $"Core: {coreMode}",
            { State: CoreLinkState.Connected } => "Core: connected",
            { State: CoreLinkState.Connecting } => "Core: connecting…",
            { State: CoreLinkState.AuthRejected } => "AUTH REJECTED",
            _ => "no Core connection",
        };
        _tray.Text = $"TOLIK — {_mode} · {link}";
    }

    private void SetInteractive(bool interactive)
    {
        _interactive = interactive;
        ModePolicy policy = ModePolicyTable.GetSafe(_mode);

        if (_window is not null)
        {
            _window.SetInteractive(interactive, policy);
        }

        if (_interactiveItem is not null)
        {
            _interactiveItem.Checked = interactive;
        }
    }

    private void SetDebugOverlay(bool enabled)
    {
        _debugOverlay = enabled;
        if (_debugItem is not null)
        {
            _debugItem.Checked = enabled;
        }
        if (_window is not null)
        {
            _window.CephalonHost.SetDebugOverlay(enabled);
        }
    }

    private void ToggleOverlayVisibility()
    {
        if (_window is null || !_window.IsLoaded)
        {
            return;
        }
        if (_window.Visibility == Visibility.Visible)
        {
            _window.Hide();
            if (_showItem is not null)
            {
                _showItem.Text = "Show overlay";
            }
        }
        else
        {
            _window.Show();
            if (_showItem is not null)
            {
                _showItem.Text = "Hide overlay";
            }
        }
    }

    private void ExitApplication()
    {
        Shutdown();
    }
}
