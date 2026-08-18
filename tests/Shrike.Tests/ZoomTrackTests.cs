using Shrike.Core.Recording;

namespace Shrike.Tests;

public class ZoomTrackTests
{
    private const int Fps = 10, W = 1000, H = 1000; // 100ms/frame, square frame for easy centre maths

    // Ease-in 300ms, hold 300ms->700ms, ease-out to 1000ms; 2x centred at (0.6, 0.4).
    private static ZoomEvent Event() => new(StartMs: 0, EndMs: 1000, CenterX: 0.6, CenterY: 0.4, Zoom: 2.0, EaseInMs: 300, EaseOutMs: 300);

    [Fact]
    public void Empty_track_is_all_full_frame()
    {
        var vps = ZoomTrack.Empty.Resolve(frameCount: 5, Fps, W, H);
        Assert.Equal(5, vps.Length);
        Assert.All(vps, vp => Assert.Equal(new ZoomViewport(0, 0, W, H), vp));
    }

    [Fact]
    public void Hold_reaches_peak_zoom_centred_on_the_focus()
    {
        var track = new ZoomTrack([Event()]);
        var vps = track.Resolve(frameCount: 10, Fps, W, H); // frames at 0,100,...,900ms

        var hold = vps[5]; // t=500ms, inside the hold
        Assert.Equal(W / 2.0, hold.Width, precision: 6);   // 2x → half-width crop
        Assert.Equal(H / 2.0, hold.Height, precision: 6);
        Assert.Equal(0.6 * W - W / 4.0, hold.X, precision: 6); // centred on (600,400): x=600-250=350
        Assert.Equal(0.4 * H - H / 4.0, hold.Y, precision: 6); // y=400-250=150
    }

    [Fact]
    public void Outside_the_span_is_full_frame()
    {
        var track = new ZoomTrack([new ZoomEvent(300, 700, 0.5, 0.5, 2.0, 100, 100)]);
        var vps = track.Resolve(frameCount: 10, Fps, W, H);
        Assert.Equal(new ZoomViewport(0, 0, W, H), vps[0]); // t=0, before start
        Assert.Equal(new ZoomViewport(0, 0, W, H), vps[9]); // t=900, after end
        Assert.True(vps[5].Width < W); // t=500, inside → zoomed
    }

    [Fact]
    public void Ease_in_ramps_zoom_up_monotonically()
    {
        var e = Event();
        // Across the ease-in window the magnification rises 1 -> 2, monotonically.
        double prev = 0;
        for (long t = 0; t <= 300; t += 50)
        {
            var z = e.ZoomAt(t);
            Assert.True(z >= prev, $"zoom dipped at t={t}");
            Assert.InRange(z, 1.0, 2.0);
            prev = z;
        }
        Assert.Equal(2.0, e.ZoomAt(300), precision: 6); // full by the end of ease-in
    }

    [Fact]
    public void Overlong_easing_scales_to_a_triangle_without_breaking()
    {
        // Ease-in + ease-out (900+900) exceed the 1000ms span; must stay finite and within [1, Zoom].
        var e = new ZoomEvent(0, 1000, 0.5, 0.5, 3.0, 900, 900);
        for (long t = 0; t <= 1000; t += 100)
            Assert.InRange(e.ZoomAt(t), 1.0, 3.0);
        Assert.True(e.ZoomAt(500) > 1.0); // peaks somewhere in the middle
    }

    [Fact]
    public void Overlapping_events_take_the_greater_zoom()
    {
        var small = new ZoomEvent(0, 1000, 0.5, 0.5, 1.5, 0, 0);
        var big = new ZoomEvent(0, 1000, 0.2, 0.8, 2.5, 0, 0);
        var vps = new ZoomTrack([small, big]).Resolve(frameCount: 10, Fps, W, H);

        // At the hold, the 2.5x event wins: crop width = W/2.5, centred on (0.2,0.8).
        Assert.Equal(W / 2.5, vps[5].Width, precision: 4);
        Assert.Equal(0.2 * W - (W / 2.5) / 2.0, vps[5].X, precision: 4);
    }

    [Fact]
    public void Resolved_viewports_feed_the_zoom_compositor()
    {
        // End-to-end shape check: the resolver's output drives ZoomCompositor like AutoZoom.Viewports does.
        var vps = new ZoomTrack([Event()]).Resolve(frameCount: 10, Fps, 120, 90);
        var buf = new byte[120 * 90 * 4];
        for (var i = 0; i < buf.Length; i += 4) { buf[i] = buf[i + 1] = buf[i + 2] = 128; buf[i + 3] = 255; }
        var before = (byte[])buf.Clone();

        new ZoomCompositor(vps).Compose(buf, 120, 90, 5); // a zoomed frame → resample runs
        // A flat grey field resamples to the same grey, so assert the call is a well-formed no-crash op:
        Assert.Equal(before.Length, buf.Length);
        Assert.All(buf, b => Assert.InRange(b, 120, 255));
    }
}
