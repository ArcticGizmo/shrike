using Shrike.Core.Capture;

namespace Shrike.Tests;

public class ThumbnailTests
{
    private static CapturedImage Solid(int w, int h, byte b, byte g, byte r)
    {
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 0] = b;
            bgra[i + 1] = g;
            bgra[i + 2] = r;
            bgra[i + 3] = 255;
        }
        return new CapturedImage(w, h, bgra, new PixelBounds(5, 6, w, h), DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Downscale_clamps_longest_side_and_preserves_aspect()
    {
        var thumb = Thumbnail.Downscale(Solid(400, 200, 0, 0, 0), maxDimension: 100);
        Assert.Equal(100, thumb.Width);
        Assert.Equal(50, thumb.Height);
    }

    [Fact]
    public void Downscale_does_not_upscale_small_images()
    {
        var src = Solid(40, 30, 0, 0, 0);
        var thumb = Thumbnail.Downscale(src, maxDimension: 160);
        Assert.Same(src, thumb); // returned as-is
    }

    [Fact]
    public void Downscale_preserves_a_solid_colour()
    {
        var thumb = Thumbnail.Downscale(Solid(300, 300, 10, 20, 30), maxDimension: 16);
        // Averaging a uniform image must reproduce the same colour everywhere.
        for (var i = 0; i < thumb.Bgra.Length; i += 4)
        {
            Assert.Equal(10, thumb.Bgra[i + 0]);
            Assert.Equal(20, thumb.Bgra[i + 1]);
            Assert.Equal(30, thumb.Bgra[i + 2]);
            Assert.Equal(255, thumb.Bgra[i + 3]);
        }
    }

    [Fact]
    public void Downscale_carries_the_source_rectangle()
    {
        var thumb = Thumbnail.Downscale(Solid(300, 300, 0, 0, 0), maxDimension: 16);
        Assert.Equal(new PixelBounds(5, 6, 300, 300), thumb.Source);
    }

    [Fact]
    public void Downscale_keeps_at_least_one_pixel_on_extreme_ratios()
    {
        // A very wide, 1px-tall strip must not collapse to zero height.
        var thumb = Thumbnail.Downscale(Solid(1000, 1, 0, 0, 0), maxDimension: 100);
        Assert.Equal(100, thumb.Width);
        Assert.Equal(1, thumb.Height);
    }
}
