using Shrike.Core.Recording;

namespace Shrike.Tests;

public class CaptionCompositorTests
{
    // BGRA helpers — a "solid" sprite is fully opaque, so premultiplied == straight.
    private static byte[] SolidFrame(int w, int h) // opaque black
    {
        var b = new byte[w * h * 4];
        for (var i = 0; i < b.Length; i += 4) b[i + 3] = 255;
        return b;
    }

    private static CaptionSprite Solid(int w, int h, byte bl, byte gr, byte re, int x, int y)
    {
        var b = new byte[w * h * 4];
        for (var i = 0; i < b.Length; i += 4) { b[i] = bl; b[i + 1] = gr; b[i + 2] = re; b[i + 3] = 255; }
        return new CaptionSprite(b, w, h, x, y);
    }

    private static (byte B, byte G, byte R, byte A) Px(byte[] frame, int w, int x, int y)
    {
        var i = (y * w + x) * 4;
        return (frame[i], frame[i + 1], frame[i + 2], frame[i + 3]);
    }

    [Fact]
    public void Blits_the_active_cue_sprite_at_its_position()
    {
        var frame = SolidFrame(4, 4);
        var red = Solid(2, 2, 0, 0, 255, x: 1, y: 1);
        var comp = new CaptionCompositor([new CaptionFrame(0, 1.0)], [red]);

        comp.Compose(frame, 4, 4, 0);

        Assert.Equal((0, 0, 255, 255), Px(frame, 4, 1, 1)); // red landed at the sprite origin
        Assert.Equal((0, 0, 255, 255), Px(frame, 4, 2, 2)); // and across its 2×2 extent
        Assert.Equal((0, 0, 0, 255), Px(frame, 4, 0, 0));   // outside the sprite: untouched black
        Assert.Equal((0, 0, 0, 255), Px(frame, 4, 3, 3));
    }

    [Fact]
    public void Frame_alpha_scales_the_blend()
    {
        var frame = SolidFrame(2, 2);
        var red = Solid(2, 2, 0, 0, 255, 0, 0);
        var comp = new CaptionCompositor([new CaptionFrame(0, 0.5)], [red]);

        comp.Compose(frame, 2, 2, 0);

        // dst = src*0.5 + dst*(1 - 1*0.5): red 255*0.5 = ~127 over black.
        var (b, g, r, a) = Px(frame, 2, 0, 0);
        Assert.Equal(0, b);
        Assert.Equal(0, g);
        Assert.InRange(r, 126, 128);
        Assert.Equal(255, a);
    }

    [Fact]
    public void Inactive_frame_draws_nothing()
    {
        var frame = SolidFrame(2, 2);
        var comp = new CaptionCompositor([CaptionFrame.Inactive], [Solid(2, 2, 0, 0, 255, 0, 0)]);
        comp.Compose(frame, 2, 2, 0);
        Assert.Equal((0, 0, 0, 255), Px(frame, 2, 0, 0)); // still black
    }

    [Fact]
    public void Cue_index_selects_the_right_sprite()
    {
        var frame = SolidFrame(2, 2);
        var red = Solid(2, 2, 0, 0, 255, 0, 0);
        var green = Solid(2, 2, 0, 255, 0, 0, 0);
        var comp = new CaptionCompositor([new CaptionFrame(1, 1.0)], [red, green]); // frame points at cue 1

        comp.Compose(frame, 2, 2, 0);
        Assert.Equal((0, 255, 0, 255), Px(frame, 2, 0, 0)); // green, not red
    }

    [Fact]
    public void Sprite_partly_off_frame_is_clipped_not_crashing()
    {
        var frame = SolidFrame(3, 3);
        var red = Solid(2, 2, 0, 0, 255, x: 2, y: 2); // only its top-left pixel lands at (2,2)
        var comp = new CaptionCompositor([new CaptionFrame(0, 1.0)], [red]);

        comp.Compose(frame, 3, 3, 0); // must not throw
        Assert.Equal((0, 0, 255, 255), Px(frame, 3, 2, 2));
    }

    [Fact]
    public void Empty_frames_or_missing_sprite_are_safe()
    {
        var frame = SolidFrame(2, 2);
        new CaptionCompositor([], []).Compose(frame, 2, 2, 0);                 // no frames
        new CaptionCompositor([new CaptionFrame(5, 1.0)], []).Compose(frame, 2, 2, 0); // cue index out of range
        new CaptionCompositor([new CaptionFrame(0, 1.0)], [null]).Compose(frame, 2, 2, 0); // null sprite
        Assert.Equal((0, 0, 0, 255), Px(frame, 2, 0, 0)); // untouched throughout
    }
}
