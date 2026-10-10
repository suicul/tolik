using System.Drawing;
using System.Windows;
using TOLIK.Desktop.Presence;
using WinForms = System.Windows.Forms;

// WinForms also defines Application — pin the WPF one (the XAML partial already uses it).
using Application = System.Windows.Application;
// The tray glyph is drawn with GDI+, so pin the System.Drawing Point here.
using Point = System.Drawing.Point;

namespace TOLIK.Desktop;

/// <summary>
/// Composition root for the Phase 1 desktop presence: overlay window + tray icon +
/// mode policy + global hotkey. Deliberately small: TOLIK is a background system
/// (AGENTS §34), and this app contains NO identity, memory, permissions or network
/// code — it is a local, honest visual presence with no Core connection yet.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\TOLIK.Desktop.Cephalon";
    private const string HotkeyText = "Ctrl+Alt+T";

    private Mutex? _singleInstance;
    private CephalonWindow? _window;
    private WinForms.NotifyIcon? _tray;
    private Icon? _trayIcon;

    private WinForms.ToolStripMenuItem? _showItem;
    private WinForms.ToolStripMenuItem? _interactiveItem;
    private WinForms.ToolStripMenuItem? _debugItem;
    private readonly Dictionary<PresenceMode, WinForms.ToolStripMenuItem> _modeItems = new();

    private PresenceMode _mode = PresenceMode.Normal;
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

        SetMode(PresenceMode.Normal);
        SetInteractive(false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
            Text = "TOLIK — local presence, no Core connection",
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

    private void SetMode(PresenceMode mode)
    {
        _mode = mode;
        ModePolicy policy = ModePolicyTable.GetSafe(mode);

        if (_window is not null)
        {
            _window.SetInteractive(_interactive, policy);
            _window.CephalonHost.ApplyMode(mode, policy);
        }

        foreach (var item in _modeItems)
        {
            item.Value.Checked = item.Key == mode;
        }

        if (_tray is not null)
        {
            _tray.Text = $"TOLIK — {mode} (local, no Core connection)";
        }
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
