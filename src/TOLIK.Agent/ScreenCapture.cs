using System.Runtime.InteropServices;
using TOLIK.Contracts;

namespace TOLIK.Agent;

/// <summary>
/// CU-1 Screen capture (the registered CaptureScreen capability's provider): the
/// PRIMARY screen captured via the classic BitBlt P/Invoke route (gdi32 — zero
/// NuGet, Windows-only, always available in the desktop agent's session). The
/// capture writes a local BMP under the agent's allowed roots and the STEP RESULT
/// is the path + the dimensions — the image itself NEVER travels the wire (§50
/// privacy by construction: the screen contains private data; the permission is the
/// existing AuthorizationPolicy, the indicator is the audit (AuditAlways), the
/// revocation is the TOLIK_ALLOWED_ROOTS path guard re-checked here — defense in
/// depth, §23: the wire could carry anything).
///
/// The 24bpp BMP (BITMAPFILEHEADER + BITMAPINFOHEADER + bottom-up rows) — the
/// universally readable, dependency-free format; PNG would need a library and the
/// plan's dependency philosophy says no. The file stays local for the future
/// vision/planner consumers (post-Genesis).
/// </summary>
internal static class ScreenCapture
{
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hobj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint op);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint Srccopy = 0x00CC0020;

    /// <summary>
    /// Captures the primary screen to a 24bpp BMP under the (already root-guarded)
    /// directory. The directory is re-resolved through the path guard (the wire
    /// argument is untrusted — §23). Returns the honest result string with the path
    /// and the dimensions; throws on any failure (the step fails honestly).
    /// </summary>
    public static string Capture(string? directoryHint, CancellationToken cancellationToken = default)
    {
        var width = GetSystemMetrics(SmCxScreen);
        var height = GetSystemMetrics(SmCyScreen);
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"screen capture: the primary screen metrics are invalid ({width}x{height}).");

        // The allowed-roots re-resolution: the wire argument is untrusted (§23) —
        // the guard throws UnauthorizedAccessException on ANY violation (empty roots,
        // outside allowlist, link escapes) — fail closed, §50.
        var roots = (Environment.GetEnvironmentVariable("TOLIK_ALLOWED_ROOTS") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var directory = AgentPathGuard.ResolveAllowedPath(
            string.IsNullOrWhiteSpace(directoryHint) ? "." : directoryHint, roots);

        Directory.CreateDirectory(directory);
        cancellationToken.ThrowIfCancellationRequested();

        var stamp = DateTimeOffset.UtcNow;
        var filePath = Path.Combine(directory, $"capture-{stamp:yyyyMMdd-HHmmss-fff}.bmp");

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
            throw new InvalidOperationException("capture_screen: GetDC failed (the display is unavailable).");
        try
        {
            var memory = CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero)
                throw new InvalidOperationException("capture_screen: CreateCompatibleDC failed.");
            try
            {
                var bitmap = CreateCompatibleBitmap(screen, width, height);
                if (bitmap == IntPtr.Zero)
                    throw new InvalidOperationException("capture_screen: CreateCompatibleBitmap failed.");
                try
                {
                    var previous = SelectObject(memory, bitmap);
                    try
                    {
                        if (!BitBlt(memory, 0, 0, width, height, screen, 0, 0, Srccopy))
                            throw new InvalidOperationException("capture_screen: BitBlt failed (the display refused the read).");
                    }
                    finally
                    {
                        SelectObject(memory, previous);
                    }

                    // ONE GetDIBits call for the whole DIB — it is already bottom-up
                    // BGR (exactly the BMP pixel layout). The per-row loop was the
                    // bottleneck (a 1080-row P/Invoke sequence exceeds the task
                    // budget — the live smoke proved it).
                    var pixelData = new byte[ScreenCaptureBmp.FileSize(width, height) - ScreenCaptureBmp.HeaderSize];
                    var bmi = new BITMAPINFO { biSize = 40, biWidth = width, biHeight = height, biPlanes = 1, biBitCount = ScreenCaptureBmp.BitsPerPixel };
                    if (!GetDIBits(memory, bitmap, 0, (uint)height, pixelData, ref bmi, 0))
                        throw new InvalidOperationException("capture_screen: GetDIBits failed (the display refused the read).");
                    File.WriteAllBytes(filePath, ScreenCaptureBmp.Build(width, height, pixelData));
                }
                finally
                {
                    DeleteObject(bitmap);
                }
            }
            finally
            {
                DeleteDC(memory);
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }

        return $"path={filePath};width={width};height={height}";
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFO bmi, uint usage);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hobj);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }
}
