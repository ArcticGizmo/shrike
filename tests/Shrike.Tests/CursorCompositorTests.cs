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
}
