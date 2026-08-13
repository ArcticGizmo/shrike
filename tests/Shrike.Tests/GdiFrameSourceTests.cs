using Shrike.Core.Capture;
using Shrike.Core.Recording;

namespace Shrike.Tests;

public class GdiFrameSourceTests
{
    [Fact]
    public void Rounds_region_to_even_dimensions()
    {
        var src = new GdiFrameSource(new PixelBounds(5, 7, 101, 65));
        Assert.Equal(100, src.Width);   // odd width trimmed down
        Assert.Equal(64, src.Height);   // odd height trimmed down
    }

    [Fact]
    public void Rejects_a_degenerate_region()
    {
        Assert.Throws<ArgumentException>(() => new GdiFrameSource(new PixelBounds(0, 0, 1, 1)));
    }

    [Fact]
    public void Captures_a_frame_of_the_expected_size()
    {
        var vs = ScreenCapture.VirtualScreenBounds();
        if (vs.IsEmpty) return; // session-less agent — skip

        using var src = new GdiFrameSource(new PixelBounds(vs.X, vs.Y, 64, 48));
        var frame = src.CaptureFrame();
        Assert.Equal(src.Width * src.Height * 4, frame.Length);
    }
}
