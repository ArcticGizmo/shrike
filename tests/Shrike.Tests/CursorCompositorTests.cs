using Shrike.Core.Recording;

namespace Shrike.Tests;

public class CursorCompositorTests
{
    private const int W = 120, H = 90;

    private static SmoothedCursorTrack Track(IReadOnlyList<CursorSample> frames, IReadOnlyList<CursorClickMark>? clicks = null)
        => new(fps: 30, frames, clicks ?? []);

    private static int NonZero(byte[] bgra)
    {
        var count = 0;
        for (var i = 0; i < bgra.Length; i += 4)
            if (bgra[i] != 0 || bgra[i + 1] != 0 || bgra[i + 2] != 0) count++;
        return count;
    }

    [Fact]
    public void Cursor_is_drawn_at_the_sample_position()
    {
        var buf = new byte[W * H * 4]; // black, fully transparent
        var comp = new CursorCompositor(Track([new CursorSample(40, 30)]));
        comp.Compose(buf, W, H, 0);

        // Somewhere in the arrow's box (tip at 40,30, extending down-right) there's a light fill pixel.
        var foundFill = false;
        for (var y = 30; y < 56 && !foundFill; y++)
            for (var x = 40; x < 60 && !foundFill; x++)
                if (buf[(y * W + x) * 4 + 2] > 200) foundFill = true; // R channel
        Assert.True(foundFill, "expected a light cursor pixel near the sample position");

        // Far from the cursor, nothing was touched.
        var far = (80 * W + 105) * 4;
        Assert.Equal(0, buf[far] | buf[far + 1] | buf[far + 2]);
    }

    [Fact]
    public void Empty_track_is_a_noop()
    {
        var buf = new byte[W * H * 4];
        var comp = new CursorCompositor(Track([]));
        comp.Compose(buf, W, H, 0);
        Assert.Equal(0, NonZero(buf));
    }

    [Fact]
    public void A_click_adds_a_ripple()
    {
        var frames = new[] { new CursorSample(60, 45) };

        var plain = new byte[W * H * 4];
        new CursorCompositor(Track(frames)).Compose(plain, W, H, 5);

        var withClick = new byte[W * H * 4];
        new CursorCompositor(Track(frames, [new CursorClickMark(0, MouseButtonKind.Left)])).Compose(withClick, W, H, 5);

        // Frame 5 is within the ripple's lifetime, so the ring paints pixels the plain cursor doesn't.
        Assert.True(NonZero(withClick) > NonZero(plain),
            $"expected ripple pixels; withClick={NonZero(withClick)} plain={NonZero(plain)}");
    }

    [Fact]
    public void Zoom_magnifies_the_frame_and_centres_the_cursor()
    {
        // A horizontal gradient so a resample is detectable, cursor near a corner.
        var gradient = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                gradient[i] = gradient[i + 1] = gradient[i + 2] = (byte)(x * 255 / (W - 1));
                gradient[i + 3] = 255;
            }
        var before = (byte[])gradient.Clone();

        var frames = new[] { new CursorSample(30, 20) };
        var comp = new CursorCompositor(Track(frames), zoomCurve: [2.0]);
        comp.Compose(gradient, W, H, 0);

        // The 2× crop changed the frame content away from the cursor (a real resample happened).
        var probe = (5 * W + 90) * 4;
        Assert.NotEqual(before[probe], gradient[probe]);

        // With the viewport centred on the cursor, the cursor lands near the middle of the frame,
        // not back at (30,20). Check the corner where the cursor's tip would be without zoom is untouched-ish
        // and a light pixel appears around the frame centre.
        var foundCentre = false;
        for (var y = H / 2 - 4; y < H / 2 + 20 && !foundCentre; y++)
            for (var x = W / 2 - 4; x < W / 2 + 20 && !foundCentre; x++)
                if (gradient[(y * W + x) * 4 + 2] > 200) foundCentre = true;
        Assert.True(foundCentre, "expected the cursor near the frame centre when zoomed");
    }

    [Fact]
    public void Ripple_is_gone_after_its_lifetime()
    {
        var frames = new[] { new CursorSample(60, 45) };
        var clicked = Track(frames, [new CursorClickMark(0, MouseButtonKind.Left)]);

        var plain = new byte[W * H * 4];
        new CursorCompositor(Track(frames)).Compose(plain, W, H, 100); // long after any ripple

        var late = new byte[W * H * 4];
        new CursorCompositor(clicked).Compose(late, W, H, 100);

        Assert.Equal(NonZero(plain), NonZero(late)); // only the cursor remains in both
    }

    [Fact]
    public void Ripple_disabled_draws_no_ring()
    {
        var frames = new[] { new CursorSample(60, 45) };
        var clicked = Track(frames, [new CursorClickMark(0, MouseButtonKind.Left)]);

        // Same click, same frame within the ripple's lifetime — but ripples off.
        var withRipple = new byte[W * H * 4];
        new CursorCompositor(clicked, CursorStyle.Default).Compose(withRipple, W, H, 5);

        var noRipple = new byte[W * H * 4];
        new CursorCompositor(clicked, CursorStyle.Default with { RippleEnabled = false }).Compose(noRipple, W, H, 5);

        // Off means only the cursor is drawn — fewer touched pixels, and it matches the no-click cursor.
        var cursorOnly = new byte[W * H * 4];
        new CursorCompositor(Track(frames)).Compose(cursorOnly, W, H, 5);

        Assert.True(NonZero(noRipple) < NonZero(withRipple));
        Assert.Equal(NonZero(cursorOnly), NonZero(noRipple));
    }

    [Fact]
    public void ForExport_scales_the_cursor_with_frame_height()
    {
        // ~24px at 1080p; smaller frames get a smaller cursor, larger frames a larger one (clamped).
        Assert.Equal(24, CursorStyle.BaseHeightFor(1080));
        Assert.True(CursorStyle.ForExport(480).Height < CursorStyle.ForExport(1080).Height);
        Assert.True(CursorStyle.ForExport(2160).Height > CursorStyle.ForExport(1080).Height);

        // Size scale multiplies it, and the ripple geometry scales in proportion.
        var small = CursorStyle.ForExport(1080, 0.5);
        var large = CursorStyle.ForExport(1080, 2.0);
        Assert.True(large.Height > small.Height);
        Assert.True(large.RippleEndRadius > small.RippleEndRadius);

        // Ripple flag is carried through.
        Assert.False(CursorStyle.ForExport(1080, 1.0, rippleEnabled: false).RippleEnabled);
    }
}
