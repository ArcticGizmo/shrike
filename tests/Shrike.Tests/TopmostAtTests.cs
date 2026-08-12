using Shrike.App.Native;
using Shrike.Core.Capture;

namespace Shrike.Tests;

public class TopmostAtTests
{
    // List order is Z order (topmost first), as EnumWindows returns it.
    private static readonly IReadOnlyList<PixelBounds> Windows =
    [
        new PixelBounds(100, 100, 200, 200), // top
        new PixelBounds(0, 0, 400, 400),     // bottom (overlaps the first)
    ];

    [Fact]
    public void Returns_topmost_window_when_overlapping()
    {
        // Point inside both — must pick the first (topmost).
        Assert.Equal(Windows[0], TopLevelWindows.TopmostAt(Windows, 150, 150));
    }

    [Fact]
    public void Returns_lower_window_when_only_it_contains_the_point()
    {
        Assert.Equal(Windows[1], TopLevelWindows.TopmostAt(Windows, 20, 20));
    }

    [Fact]
    public void Returns_null_when_no_window_contains_the_point()
    {
        Assert.Null(TopLevelWindows.TopmostAt(Windows, 900, 900));
    }

    [Fact]
    public void Right_and_bottom_edges_are_exclusive()
    {
        // Point exactly on the bottom window's right/bottom edge is outside it.
        Assert.Null(TopLevelWindows.TopmostAt([new PixelBounds(0, 0, 10, 10)], 10, 10));
    }
}
