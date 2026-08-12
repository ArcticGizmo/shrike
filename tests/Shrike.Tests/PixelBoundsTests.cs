using Shrike.Core.Capture;

namespace Shrike.Tests;

public class PixelBoundsTests
{
    [Fact]
    public void Normalized_flips_negative_extents()
    {
        var n = new PixelBounds(100, 100, -40, -20).Normalized();
        Assert.Equal(new PixelBounds(60, 80, 40, 20), n);
    }

    [Fact]
    public void FromCorners_orders_the_corners()
    {
        Assert.Equal(new PixelBounds(10, 20, 90, 60), PixelBounds.FromCorners(100, 80, 10, 20));
    }

    [Fact]
    public void IsEmpty_when_zero_or_negative()
    {
        Assert.True(new PixelBounds(0, 0, 0, 10).IsEmpty);
        Assert.True(new PixelBounds(0, 0, 10, -1).IsEmpty);
        Assert.False(new PixelBounds(0, 0, 1, 1).IsEmpty);
    }

    [Fact]
    public void Intersect_returns_overlap()
    {
        var a = new PixelBounds(0, 0, 100, 100);
        var b = new PixelBounds(50, 50, 100, 100);
        Assert.Equal(new PixelBounds(50, 50, 50, 50), a.Intersect(b));
    }

    [Fact]
    public void Intersect_is_empty_when_disjoint()
    {
        var a = new PixelBounds(0, 0, 10, 10);
        var b = new PixelBounds(100, 100, 10, 10);
        Assert.True(a.Intersect(b).IsEmpty);
    }

    [Fact]
    public void Negative_origin_supported_for_secondary_monitors()
    {
        // A monitor left of the primary lives at negative X in virtual-screen space.
        var b = new PixelBounds(-1920, 0, 1920, 1080);
        Assert.Equal(0, b.Right);
        Assert.False(b.IsEmpty);
    }
}
