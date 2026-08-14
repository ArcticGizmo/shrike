using Shrike.Core.Capture;

namespace Shrike.Tests;

public class PixelColorTests
{
    // 4x4 image whose pixels encode their (x,y) into the R and G bytes, source origin at (10,20).
    private static CapturedImage Grid(int w = 4, int h = 4, int originX = 10, int originY = 20)
    {
        var bgra = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            bgra[i + 0] = 0;            // B
            bgra[i + 1] = (byte)y;      // G = row
            bgra[i + 2] = (byte)x;      // R = col
            bgra[i + 3] = 255;          // A
        }
        return new CapturedImage(w, h, bgra, new PixelBounds(originX, originY, w, h), DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void SampleColor_reads_the_pixel_at_a_source_point()
    {
        // Source (12,22) => image-local (2,2) => R=2, G=2, B=0.
        var c = Grid().SampleColor(12, 22);
        Assert.Equal(new PixelColor(2, 2, 0), c);
    }

    [Fact]
    public void SampleColor_clamps_points_outside_the_image()
    {
        // Far past the bottom-right clamps to the last pixel: local (3,3) => R=3, G=3.
        var c = Grid().SampleColor(1000, 1000);
        Assert.Equal(new PixelColor(3, 3, 0), c);
    }

    [Fact]
    public void Hex_is_uppercase_and_hash_prefixed()
    {
        Assert.Equal("#3A7BD5", new PixelColor(58, 123, 213).Hex);
        Assert.Equal("#000000", new PixelColor(0, 0, 0).Hex);
        Assert.Equal("#FFFFFF", new PixelColor(255, 255, 255).Hex);
    }

    [Fact]
    public void Rgb_uses_css_form()
    {
        Assert.Equal("rgb(58, 123, 213)", new PixelColor(58, 123, 213).Rgb);
    }

    [Fact]
    public void Hsl_converts_a_mixed_colour()
    {
        Assert.Equal("hsl(215, 65%, 53%)", new PixelColor(58, 123, 213).Hsl);
    }

    [Fact]
    public void Hsl_handles_pure_red()
    {
        Assert.Equal("hsl(0, 100%, 50%)", new PixelColor(255, 0, 0).Hsl);
    }

    [Fact]
    public void Hsl_reports_grey_as_zero_saturation()
    {
        Assert.Equal("hsl(0, 0%, 50%)", new PixelColor(128, 128, 128).Hsl);
    }
}
