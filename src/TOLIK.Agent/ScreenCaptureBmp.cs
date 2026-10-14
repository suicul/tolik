namespace TOLIK.Agent;

/// <summary>
/// The bounded 24bpp BMP writer for CU-1 (the universally readable, dependency-free
/// format). PURE LAYOUT — no P/Invoke, directly unit-testable: the header geometry
/// (the two headers + the 4-byte-aligned rows), the bottom-up orientation (the
/// first row of the DIB = the bottom of the screen = the first bytes of the pixel
/// data), the BGR triplet layout (the classic GDI format). The writer faithfully
/// serializes the rows in call order; the screen orientation belongs to the capture
/// flow (GetDIBits row 0 is the bottom row).
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
    /// Builds the BMP bytes. <paramref name="readRow"/> provides each row's stride-
    /// length bytes (the BGR triplets + the padding) in screen order (row 0 = the
    /// bottom); a short row fails honestly.
    /// </summary>
    public static byte[] Build(int width, int height, Func<int, byte[]> readRow)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("The BMP dimensions must be positive.");

        var stride = RowStride(width);
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
        WriteInt32(buffer, 34, stride * height); // the pixel data size

        for (var y = 0; y < height; y++)
        {
            var row = readRow(y);
            if (row.Length < stride)
                throw new InvalidOperationException($"the row {y} is shorter than the stride ({row.Length} < {stride}).");
            Array.Copy(row, 0, buffer, HeaderSize + y * stride, stride);
        }

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
