using Shrike.Core.Recording;

namespace Shrike.Tests;

public class AutoZoomTests
{
    [Fact]
    public void No_clicks_means_no_zoom()
    {
        var z = AutoZoom.ZoomCurve([], frameCount: 60, fps: 30, ZoomConfig.Default);
        Assert.All(z, v => Assert.Equal(1.0, v, 6));
    }

    [Fact]
    public void Disabled_config_means_no_zoom()
    {
        var clicks = new[] { new CursorClickMark(30, MouseButtonKind.Left) };
        var z = AutoZoom.ZoomCurve(clicks, frameCount: 120, fps: 30, ZoomConfig.Off);
        Assert.All(z, v => Assert.Equal(1.0, v, 6));
    }

    [Fact]
    public void A_click_zooms_in_then_eases_back_out()
    {
        // 6 s @ 30fps; a click at 2 s, hold 1.6 s, ease 0.6 s.
        var clicks = new[] { new CursorClickMark(60, MouseButtonKind.Left) };
        var cfg = new ZoomConfig(Enabled: true, MaxZoom: 1.6, HoldSeconds: 1.6, EaseSeconds: 0.6);
        var z = AutoZoom.ZoomCurve(clicks, frameCount: 180, fps: 30, cfg);

        Assert.Equal(1.0, z[0], 3);              // starts un-zoomed
        Assert.True(z[75] > 1.4);                // peaked near the click's hold
        Assert.True(z[75] <= 1.6 + 1e-9);        // never exceeds MaxZoom
        Assert.Equal(1.0, z[^1], 2);             // eased back out well after the hold
        Assert.All(z, v => Assert.True(v >= 1.0)); // never below 1
    }

    [Fact]
    public void Viewport_is_the_full_frame_at_zoom_one()
    {
        var vp = AutoZoom.Viewport(1.0, 100, 50, 200, 100);
        Assert.Equal(new ZoomViewport(0, 0, 200, 100), vp);
    }

    [Fact]
    public void Viewport_centres_on_the_point_when_zoomed()
    {
        var vp = AutoZoom.Viewport(2.0, 100, 50, 200, 100);
        Assert.Equal(new ZoomViewport(50, 25, 100, 50), vp); // half-size crop centred at (100,50)
    }

    [Fact]
    public void Viewport_clamps_to_the_frame_edges()
    {
        var vp = AutoZoom.Viewport(2.0, 0, 0, 200, 100); // centre at a corner
        Assert.Equal(new ZoomViewport(0, 0, 100, 50), vp);
    }
}
