using System.Runtime.InteropServices;

namespace TOLIK.Desktop.Interop;

/// <summary>
/// Minimal Win32 interop for the overlay host: extended window styles
/// (click-through passive mode) and the global mode hotkey.
/// SetWindowLongPtrW/GetWindowLongPtrW exist on 64-bit user32 (the only
/// deployment target for TOLIK Desktop Phase 1: the owner's x64 Windows 11).
/// </summary>
internal static class NativeMethods
{
    internal const int GWL_EXSTYLE = -20;

    internal const long WS_EX_TRANSPARENT = 0x00000020L; // click-through (mouse passes to windows below)
    internal const long WS_EX_LAYERED = 0x00080000L;
    internal const long WS_EX_NOACTIVATE = 0x08000000L;  // never steals focus — TOLIK is background-first (§34)
    internal const long WS_EX_TOOLWINDOW = 0x00000080L;  // no alt-tab entry

    internal const uint MOD_ALT = 0x0001u;
    internal const uint MOD_CONTROL = 0x0002u;
    internal const uint VK_T = 0x54u; // 'T'
    internal const int HotkeyId = 0x7C01;
    internal const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
