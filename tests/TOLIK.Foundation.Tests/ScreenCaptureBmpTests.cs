using TOLIK.Agent;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// CU-1: the bounded 24bpp BMP writer (ScreenCaptureBmp) — the pure layout, no
/// P/Invoke. The contract: the file is a valid classic BMP (the "BM" magic, the
/// two headers, the 4-byte-aligned rows, the positive height = bottom-up, 24bpp
/// no alpha); the rows are serialized faithfully in call order (the reader's row 0
/// is the FIRST bytes of the pixel data — the bottom of the screen in the capture
/// flow); a short row fails honestly.
/// </summary>
public sealed class ScreenCaptureBmpTests
{
    [Fact]
    public void Header_layout_is_the_classic_bottom_up_24bpp_bmp()
    {
        // A synthetic 2x2 screen: each row carries its y in the BGR triplets.
        var bmp = ScreenCaptureBmp.Build(2, 2, y =>
        {
            var row = new byte[ScreenCaptureBmp.RowStride(2)];
            for (var x = 0; x < 2; x++)
            {
                row[x * 3] = (byte)y;     // B
                row[x * 3 + 1] = (byte)y; // G
                row[x * 3 + 2] = (byte)y; // R
            }

            return row;
        });

        Assert.Equal(ScreenCaptureBmp.FileSize(2, 2), bmp.Length);
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        // the file size at offset 2
        Assert.Equal(bmp.Length, BitConverter.ToInt32(bmp, 2));
        // the pixel data offset = 14 + 40
        Assert.Equal(54, BitConverter.ToInt32(bmp, 10));
        // the BITMAPINFOHEADER
        Assert.Equal(40, BitConverter.ToInt32(bmp, 14));
        Assert.Equal(2, BitConverter.ToInt32(bmp, 18));  // width
        Assert.Equal(2, BitConverter.ToInt32(bmp, 22));  // height: positive = bottom-up
        Assert.Equal(1, BitConverter.ToInt16(bmp, 26));  // planes
        Assert.Equal(24, BitConverter.ToInt16(bmp, 28)); // 24bpp
        Assert.Equal(ScreenCaptureBmp.RowStride(2) * 2, BitConverter.ToInt32(bmp, 34));
        // the rows are faithful in call order: row 0 (y=0) first, then row 1
        Assert.Equal(0, bmp[54]);  // the first pixel of the first row (y=0)
        Assert.Equal(0, bmp[57]);  // the second pixel of the first row
        Assert.Equal(1, bmp[54 + ScreenCaptureBmp.RowStride(2)]); // the first pixel of the second row (y=1)
    }

    [Fact]
    public void Stride_aligns_rows_to_4_bytes()
    {
        // 1 pixel wide: 3 bytes of data → 4-byte stride (1 byte of padding)
        Assert.Equal(4, ScreenCaptureBmp.RowStride(1));
        // 2 pixels: 6 bytes → 8
        Assert.Equal(8, ScreenCaptureBmp.RowStride(2));
        // 4 pixels: 12 bytes → 12 (already aligned)
        Assert.Equal(12, ScreenCaptureBmp.RowStride(4));
        // the file size accounts for the padding
        Assert.Equal(ScreenCaptureBmp.HeaderSize + ScreenCaptureBmp.RowStride(1) * 3,
            ScreenCaptureBmp.FileSize(1, 3));
    }

    [Fact]
    public void Short_row_fails_honestly()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ScreenCaptureBmp.Build(2, 2, _ => new byte[1]));
        Assert.Contains("stride", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 5)]
    public void Non_positive_dimensions_fail_closed(int width, int height)
    {
        Assert.Throws<ArgumentException>(() => ScreenCaptureBmp.Build(width, height, _ => new byte[8]));
    }
}
