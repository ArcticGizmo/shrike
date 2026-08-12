using System.Buffers.Binary;
using System.Text;
using Shrike.Core.Capture;
using Shrike.Core.Imaging;

namespace Shrike.Tests;

public class ImageCodecTests
{
    private static CapturedImage MakeImage(int w = 4, int h = 3)
    {
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 0] = 0x10; // B
            bgra[i + 1] = 0x20; // G
            bgra[i + 2] = 0x30; // R
            bgra[i + 3] = 0xFF; // A
        }
        return new CapturedImage(w, h, bgra, new PixelBounds(0, 0, w, h), DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Png_has_png_signature()
    {
        var bytes = ImageCodec.Encode(MakeImage(), ImageFormatKind.Png);
        Assert.True(bytes.Length > 8);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]); // ‰PNG
    }

    [Fact]
    public void Jpeg_has_soi_marker()
    {
        var bytes = ImageCodec.Encode(MakeImage(), ImageFormatKind.Jpeg, quality: 80);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
    }

    [Fact]
    public void Webp_has_riff_webp_container()
    {
        var bytes = ImageCodec.Encode(MakeImage(), ImageFormatKind.WebP, quality: 75);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(bytes, 8, 4));
    }

    [Fact]
    public void DibV5_header_is_124_bytes_and_sized_for_pixels()
    {
        var img = MakeImage(4, 3);
        var dib = ImageCodec.ToDibV5(img);

        Assert.Equal(124 + 4 * 3 * 4, dib.Length);
        Assert.Equal(124u, BinaryPrimitives.ReadUInt32LittleEndian(dib));            // bV5Size
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4)));       // width
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)));       // height (bottom-up)
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16)));    // BI_BITFIELDS
    }

    [Fact]
    public void DibV5_is_bottom_up_first_row_is_source_last_row()
    {
        var img = MakeImage(2, 2);
        // Make the top row distinguishable from the bottom row.
        img.Bgra[0] = 0xAA;                 // top-left B
        img.Bgra[(1 * 2 * 4) + 0] = 0xBB;   // bottom-left B (row 1)

        var dib = ImageCodec.ToDibV5(img);
        var firstPixelB = dib[124]; // first pixel of DIB = bottom row of image
        Assert.Equal(0xBB, firstPixelB);
    }

    [Fact]
    public void Extensions_match_formats()
    {
        Assert.Equal(".png", ImageCodec.Extension(ImageFormatKind.Png));
        Assert.Equal(".jpg", ImageCodec.Extension(ImageFormatKind.Jpeg));
        Assert.Equal(".webp", ImageCodec.Extension(ImageFormatKind.WebP));
    }
}
