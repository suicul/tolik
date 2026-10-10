using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using TOLIK.Desktop.Interop;
using TOLIK.Desktop.Presence;
using TOLIK.Desktop.Rendering;

// WinForms also defines Cursors — pin the WPF one.
using Cursors = System.Windows.Input.Cursors;

namespace TOLIK.Desktop;

/// <summary>
/// The overlay host window: transparent, borderless, always-on-top, no taskbar,
/// never activates. Two interaction postures:
/// - Passive (default): click-through (WS_EX_TRANSPARENT) — the eye watches, nothing blocks the user.
/// - Interactive: clickable and draggable; toggled by the tray menu, Ctrl+Alt+T, or double-click.
/// The window holds no state of consequence: it is a surface for the renderer.
/// </summary>
public partial class CephalonWindow : Window
{
    private const double RightMargin = 48.0;

    private bool _interactive;
    private bool _hotkeyRegistered;
    private HwndSource? _hwndSource;

    /// <summary>Raised when the user requests an interactive/passive toggle (hotkey or double-click).</summary>
    public event Action? InteractiveToggleRequested;

    public CephalonWindow()
    {
        InitializeComponent();
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseDoubleClick += OnMouseDoubleClick;
    }

    public CephalonHost CephalonHost => Host;

    public bool IsInteractive => _interactive;

    public bool HotkeyRegistered => _hotkeyRegistered;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwndSource = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwndSource.AddHook(WndProc);

        IntPtr hwnd = _hwndSource.Handle;

        // Primary monitor, right side, vertically centered (multi-monitor aware:
        // the user can drag it onto any monitor and it stays).
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - RightMargin;
        Top = work.Top + ((work.Height - Height) / 2.0);

        // Start passive: TOLIK watches, never obstructs.
        ApplyClickThrough(passive: true);

        _hotkeyRegistered = NativeMethods.RegisterHotKey(
            hwnd, NativeMethods.HotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, NativeMethods.VK_T);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            if (_hotkeyRegistered)
            {
                NativeMethods.UnregisterHotKey(_hwndSource.Handle, NativeMethods.HotkeyId);
                _hotkeyRegistered = false;
            }
            _hwndSource = null;
        }
        base.OnClosed(e);
    }

    /// <summary>Sets the interaction posture. In ForcePassive modes the policy wins over the user flag.</summary>
    public void SetInteractive(bool interactive, ModePolicy policy)
    {
        _interactive = interactive && !policy.ForcePassive;
        ApplyClickThrough(passive: !_interactive);
        Cursor = _interactive ? Cursors.Hand : Cursors.Arrow;
    }

    private void ApplyClickThrough(bool passive)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        if (passive)
        {
            exStyle |= NativeMethods.WS_EX_TRANSPARENT;
        }
        else
        {
            exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
        }
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_interactive)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove can race with the mouse-up; harmless.
            }
        }
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_interactive)
        {
            InteractiveToggleRequested?.Invoke();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == NativeMethods.HotkeyId)
        {
            InteractiveToggleRequested?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }
}
