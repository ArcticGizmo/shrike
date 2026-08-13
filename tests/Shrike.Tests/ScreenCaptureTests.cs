using Shrike.Core.Capture;

namespace Shrike.Tests;

/// <summary>
/// Exercises the native GDI capture path. Skips gracefully when there is no desktop to capture
/// (a session-less CI agent), so it verifies real BitBlt behaviour locally without breaking headless runs.
/// </summary>
public class ScreenCaptureTests
{
    [Fact]
    public void Virtual_screen_bounds_are_non_empty_on_a_desktop()
    {
        var bounds = ScreenCapture.VirtualScreenBounds();
        if (bounds.IsEmpty) return; // no interactive desktop — nothing to assert
        Assert.True(bounds.Width > 0 && bounds.Height > 0);
    }

    [Fact]
    public void Captures_the_requested_region_shape_with_opaque_alpha()
    {
        var vs = ScreenCapture.VirtualScreenBounds();
        if (vs.IsEmpty) return; // session-less agent — skip

        var region = new PixelBounds(vs.X, vs.Y, Math.Min(16, vs.Width), Math.Min(16, vs.Height));
        var image = ScreenCapture.Capture(region);

        Assert.Equal(region.Width, image.Width);
        Assert.Equal(region.Height, image.Height);
        Assert.Equal(region.Width * region.Height * 4, image.Bgra.Length);

        for (var i = 3; i < image.Bgra.Length; i += 4)
            Assert.Equal(255, image.Bgra[i]); // alpha forced opaque
    }

    [Fact]
    public void Empty_region_throws()
    {
        Assert.Throws<ArgumentException>(() => ScreenCapture.Capture(new PixelBounds(0, 0, 0, 0)));
    }
}
