using Shrike.Core.Capture;

namespace Shrike.Tests;

public class CapturedImageCropTests
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
    public void Crop_returns_requested_shape_and_source()
    {
        var cropped = Grid().Crop(new PixelBounds(11, 21, 2, 2));
        Assert.Equal(2, cropped.Width);
        Assert.Equal(2, cropped.Height);
        Assert.Equal(new PixelBounds(11, 21, 2, 2), cropped.Source);
    }

    [Fact]
    public void Crop_copies_the_correct_pixels()
    {
        // Crop the single pixel at source (12,22) => image-local (2,2) => R=2, G=2.
        var cropped = Grid().Crop(new PixelBounds(12, 22, 1, 1));
        Assert.Equal(0, cropped.Bgra[0]);   // B
        Assert.Equal(2, cropped.Bgra[1]);   // G = row 2
        Assert.Equal(2, cropped.Bgra[2]);   // R = col 2
    }

    [Fact]
    public void Crop_clamps_to_the_image()
    {
        // Ask for more than exists past the bottom-right; result clamps to the overlap.
        var cropped = Grid().Crop(new PixelBounds(12, 22, 10, 10));
        Assert.Equal(2, cropped.Width);
        Assert.Equal(2, cropped.Height);
    }

    [Fact]
    public void Crop_throws_when_disjoint()
    {
        Assert.Throws<ArgumentException>(() => Grid().Crop(new PixelBounds(100, 100, 4, 4)));
    }
}
