namespace TOLIK.Agent;

/// <summary>
/// The bounded 24bpp BMP writer for CU-1 (the universally readable, dependency-free
/// format). PURE LAYOUT — no P/Invoke, directly unit-testable: the header geometry
/// (the two headers + the 4-byte-aligned rows) and the faithful pixel-data copy.
/// The capture flow reads the whole DIB in ONE GetDIBits call — the DIB is already
/// bottom-up BGR (exactly the BMP pixel layout), so the file is the headers + the
/// DIB bytes. A single call instead of a per-row loop (a 1080-row per-row P/Invoke
/// sequence is slower than the task budget — the live smoke proved it).
/// </summary>
public static class ScreenCaptureBmp
{
    public const int HeaderSize = 14 + 40; // BITMAPFILEHEADER + BITMAPINFOHEADER
    public const ushort BmpMagic = 0x4D42; // "BM"
    public const int BitsPerPixel = 24; // no alpha — the classic screen format

    /// <summary>The 4-byte-aligned row stride (a 3-byte-per-pixel row padded).</summary>
    public static int RowStride(int width) => ((width * (BitsPerPixel / 8)) + 3) & ~3;

    /// <summary>The total file size for the given dimensions.</summary>
    public static int FileSize(int width, int height) => HeaderSize + RowStride(width) * height;

    /// <summary>
    /// Builds the BMP bytes from the DIB pixel data (bottom-up BGR rows, the stride
    /// exactly as RowStride). A short buffer fails honestly.
    /// </summary>
    public static byte[] Build(int width, int height, byte[] pixelData)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("The BMP dimensions must be positive.");

        var stride = RowStride(width);
        var pixelBytes = stride * height;
        if (pixelData is null || pixelData.Length < pixelBytes)
            throw new ArgumentException("The pixel data is shorter than the DIB geometry requires.");

        var buffer = new byte[FileSize(width, height)];

        // BITMAPFILEHEADER (14 bytes)
        buffer[0] = (byte)(BmpMagic & 0xFF);
        buffer[1] = (byte)(BmpMagic >> 8);
        WriteInt32(buffer, 2, buffer.Length);
        WriteInt32(buffer, 10, HeaderSize); // the pixel data offset

        // BITMAPINFOHEADER (40 bytes)
        WriteInt32(buffer, 14, 40);       // the header size
        WriteInt32(buffer, 18, width);
        WriteInt32(buffer, 22, height);    // positive = bottom-up
        WriteInt16(buffer, 26, 1);         // the planes
        WriteInt16(buffer, 28, BitsPerPixel);
        WriteInt32(buffer, 34, pixelBytes); // the pixel data size

        Array.Copy(pixelData, 0, buffer, HeaderSize, pixelBytes);
        return buffer;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteInt16(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }
}
