using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace TOLIK.Desktop.Presence;

/// <summary>
/// DesktopWatcher (CE-4 — PLAN REFORM PRG-2b): observes the SAFE desktop facts only —
/// the foreground window's process name and whether it covers the screen. NO window
/// content, NO text, NO screenshots (директива §12: "не следи за содержимым окон без
/// необходимости"; §50: privacy first). The game-process list comes from
/// TOLIK_GAME_PROCESSES (the M8 pattern: the comma-separated, case-insensitive).
/// The interop surface is minimal and additive; the pure matcher is unit-tested.
/// </summary>
public static class DesktopWatcher
{
    /// <summary>The observed safe desktop state: what process is in the foreground and
    /// whether its window covers the primary screen (the fullscreen proxy).</summary>
    public readonly record struct DesktopObservation(bool Fullscreen, string? ProcessName)
    {
        public static DesktopObservation None { get; } = new(false, null);
    }

    /// <summary>One snapshot: the foreground window's process + the fullscreen check.</summary>
    public static DesktopObservation Observe()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return DesktopObservation.None;

            _ = GetWindowThreadProcessId(foreground, out var processId);
            if (processId == 0) return DesktopObservation.None;
            var processName = GetProcessName(processId);

            var fullscreen = false;
            if (GetWindowRect(foreground, out var rect))
            {
                // The primary screen bounds (the virtual-screen multi-monitor cases are
                // a refinement; the primary covers the common game/presentation case).
                var screenW = SystemParameters.PrimaryScreenWidth;
                var screenH = SystemParameters.PrimaryScreenHeight;
                const double margin = 4.0; // the borderless fullscreen may exceed by a few px
                fullscreen = rect.Left <= margin && rect.Top <= margin &&
                             rect.Right >= screenW - margin && rect.Bottom >= screenH - margin;
            }
            return new DesktopObservation(fullscreen, processName);
        }
        catch
        {
            return DesktopObservation.None; // the observation is best-effort — never crashes the host
        }
    }

    /// <summary>Pure: does the process match the game list (TOLIK_GAME_PROCESSES —
    /// the comma/semicolon-separated, case-insensitive, name-without-.exe)?</summary>
    public static bool IsGameProcess(string? processName, string? gameList)
    {
        if (string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(gameList))
            return false;
        var name = processName.Trim().ToLowerInvariant();
        if (name.EndsWith(".exe", StringComparison.Ordinal)) name = name[..^4]; // the observed name may carry .exe
        foreach (var raw in gameList.Split(',', ';'))
        {
            var entry = raw.Trim().ToLowerInvariant();
            if (entry.Length == 0) continue;
            if (entry.EndsWith(".exe", StringComparison.Ordinal)) entry = entry[..^4];
            if (name == entry) return true;
        }
        return false;
    }

    private static string? GetProcessName(uint processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(512);
            return QueryFullProcessImageName(handle, 0, buffer, out var size)
                ? Path.GetFileNameWithoutExtension(buffer.ToString(0, (int)size))
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ── The minimal interop (additive; the existing NativeMethods stay untouched) ──

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, out uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
