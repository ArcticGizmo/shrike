using Shrike.Core.Annotations;
using Shrike.Core.Capture;

namespace Shrike.Tests;

public class RedactionTests
{
    // A 6x6 image where every pixel is a recognizable non-black colour.
    private static CapturedImage SecretImage(int w = 6, int h = 6)
    {
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 0] = 0x11; // B
            bgra[i + 1] = 0x22; // G
            bgra[i + 2] = 0x33; // R
            bgra[i + 3] = 0xFF; // A
        }
        return new CapturedImage(w, h, bgra, new PixelBounds(0, 0, w, h), DateTimeOffset.UnixEpoch);
    }

    private static (byte B, byte G, byte R, byte A) PixelAt(CapturedImage img, int x, int y)
    {
        var i = (y * img.Width + x) * 4;
        return (img.Bgra[i], img.Bgra[i + 1], img.Bgra[i + 2], img.Bgra[i + 3]);
    }

    [Fact]
    public void Redacted_region_is_overwritten_with_fill()
    {
        var img = SecretImage();
        var redacted = Redaction.Apply(img, [new PixelBounds(2, 2, 2, 2)]);

        // Every pixel inside the rect is the fill colour — the original is gone.
        for (var y = 2; y < 4; y++)
        for (var x = 2; x < 4; x++)
        {
            Assert.Equal((0, 0, 0, 255), PixelAt(redacted, x, y));
        }
    }

    [Fact]
    public void Pixels_outside_the_region_are_untouched()
    {
        var img = SecretImage();
        var redacted = Redaction.Apply(img, [new PixelBounds(2, 2, 2, 2)]);

        Assert.Equal((0x11, 0x22, 0x33, 0xFF), PixelAt(redacted, 0, 0));
        Assert.Equal((0x11, 0x22, 0x33, 0xFF), PixelAt(redacted, 5, 5));
    }

    [Fact]
    public void No_trace_of_the_original_remains_in_the_redacted_bytes()
    {
        var img = SecretImage();
        var redacted = Redaction.Apply(img, [new PixelBounds(0, 0, 6, 6)]); // redact everything

        // The whole buffer is the fill; the secret (0x33,0x22,0x11) bytes must be absent.
        Assert.DoesNotContain(redacted.Bgra, b => b == 0x33);
    }

    [Fact]
    public void Custom_fill_colour_is_applied()
    {
        var img = SecretImage();
        var redacted = Redaction.Apply(img, [new PixelBounds(1, 1, 2, 2)], (0xF0, 0x00, 0x00));
        // fill R=0xF0 -> stored as B=0,G=0,R=0xF0
        Assert.Equal((0x00, 0x00, 0xF0, 0xFF), PixelAt(redacted, 1, 1));
    }

    [Fact]
    public void Out_of_bounds_rects_are_clamped_or_ignored()
    {
        var img = SecretImage();
        var redacted = Redaction.Apply(img, [new PixelBounds(100, 100, 10, 10)]);
        // Nothing overlaps — image unchanged.
        Assert.Equal((0x11, 0x22, 0x33, 0xFF), PixelAt(redacted, 3, 3));
    }
}
