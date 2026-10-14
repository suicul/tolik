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

                    var bytes = ScreenCaptureBmp.Build(width, height, y =>
                    {
                        var row = new byte[ScreenCaptureBmp.RowStride(width)];
                        if (!GetDIBitsRow(memory, bitmap, row, y, width))
                            throw new InvalidOperationException($"capture_screen: the row {y} read failed.");
                        return row;
                    });
                    File.WriteAllBytes(filePath, bytes);
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

    /// <summary>Reads ONE row of the captured bitmap from the screen DC (a narrow
    /// per-row read so the provider never materializes a second full-screen buffer).</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFO bmi, uint usage);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hobj);

    private static bool GetDIBitsRow(IntPtr memory, IntPtr bitmap, byte[] row, int y, int width)
    {
        var bmi = new BITMAPINFO { biSize = (uint)ScreenCaptureBmp.HeaderSize - 14, biWidth = width, biHeight = 1, biPlanes = 1, biBitCount = ScreenCaptureBmp.BitsPerPixel };
        return GetDIBits(memory, bitmap, (uint)y, 1, row, ref bmi, 0);
    }

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
