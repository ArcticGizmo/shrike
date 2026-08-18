using Shrike.Core.Recording;

namespace Shrike.Tests;

public class CompositorChainTests
{
    private const int W = 120, H = 90;

    private static SmoothedCursorTrack Track(IReadOnlyList<CursorSample> frames) => new(30, frames, []);

    // A compositor that records the order it ran in and stamps a byte, so we can assert sequencing.
    private sealed class Marker : IFrameCompositor
    {
        private readonly List<string> _log; private readonly string _id;
        public Marker(List<string> log, string id) { _log = log; _id = id; }
        public void Compose(byte[] bgra, int w, int h, int frame) { _log.Add(_id); bgra[0]++; }
    }

    [Fact]
    public void Chain_applies_effects_in_order()
    {
        var log = new List<string>();
        var chain = new CompositorChain(new Marker(log, "a"), new Marker(log, "b"), new Marker(log, "c"));
        var buf = new byte[W * H * 4];

        chain.Compose(buf, W, H, 0);

        Assert.Equal(new[] { "a", "b", "c" }, log);
        Assert.Equal(3, buf[0]); // every effect ran on the same buffer
    }

    [Fact]
    public void Empty_chain_is_a_noop()
    {
        var buf = new byte[W * H * 4];
        new CompositorChain().Compose(buf, W, H, 0);
        Assert.All(buf, b => Assert.Equal(0, b));
    }

    // ---- AutoZoom.Viewports resolver ----

    [Fact]
    public void Viewports_are_full_frame_when_not_zoomed()
    {
        var frames = new[] { new CursorSample(10, 10), new CursorSample(20, 20) };
        var vps = AutoZoom.Viewports(frames, zoomCurve: null, W, H);

        Assert.Equal(2, vps.Length);
        Assert.All(vps, vp => Assert.Equal(new ZoomViewport(0, 0, W, H), vp));
    }

    [Fact]
    public void Viewports_centre_a_zoom_crop_on_the_cursor()
    {
        var frames = new[] { new CursorSample(60, 45) };
        var vps = AutoZoom.Viewports(frames, zoomCurve: [2.0], W, H);

        // 2× crop is half the frame, centred on (60,45) → origin (30, 22.5).
        Assert.Equal(W / 2.0, vps[0].Width, precision: 6);
        Assert.Equal(H / 2.0, vps[0].Height, precision: 6);
        Assert.Equal(30.0, vps[0].X, precision: 6);
        Assert.Equal(22.5, vps[0].Y, precision: 6);
    }

    // ---- ZoomCompositor (frame transform) + cursor overlay via the chain ----

    private static byte[] Gradient()
    {
        var g = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                g[i] = g[i + 1] = g[i + 2] = (byte)(x * 255 / (W - 1));
                g[i + 3] = 255;
            }
        return g;
    }

    [Fact]
    public void ZoomCompositor_full_frame_viewport_is_a_noop()
    {
        var buf = Gradient();
        var before = (byte[])buf.Clone();
        new ZoomCompositor([new ZoomViewport(0, 0, W, H)]).Compose(buf, W, H, 0);
        Assert.Equal(before, buf);
    }

    [Fact]
    public void Chain_zooms_the_frame_and_centres_the_cursor()
    {
        // Same expectation as the old monolithic CursorCompositor zoom test, now via the split chain.
        var frames = new[] { new CursorSample(30, 20) };
        var vps = AutoZoom.Viewports(frames, zoomCurve: [2.0], W, H);
        var chain = new CompositorChain(new ZoomCompositor(vps), new CursorCompositor(Track(frames), null, vps));

        var buf = Gradient();
        var before = (byte[])buf.Clone();
        chain.Compose(buf, W, H, 0);

        // The 2× crop changed content away from the cursor (a real resample happened).
        var probe = (5 * W + 90) * 4;
        Assert.NotEqual(before[probe], buf[probe]);

        // The cursor lands near the frame centre (viewport centred on it), not back at (30,20).
        var foundCentre = false;
        for (var y = H / 2 - 4; y < H / 2 + 20 && !foundCentre; y++)
            for (var x = W / 2 - 4; x < W / 2 + 20 && !foundCentre; x++)
                if (buf[(y * W + x) * 4 + 2] > 200) foundCentre = true;
        Assert.True(foundCentre, "expected the cursor near the frame centre when zoomed");
    }
}
