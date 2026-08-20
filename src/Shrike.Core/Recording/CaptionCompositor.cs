namespace Shrike.Core.Recording;

/// <summary>A pre-rendered caption line: a small <b>premultiplied</b> BGRA sprite (the styled text + its
/// background box) and where its top-left sits on the export frame. Baked once by the UI-side rasteriser,
/// then blitted per active frame by <see cref="CaptionCompositor"/>.</summary>
public sealed record CaptionSprite(byte[] Bgra, int Width, int Height, int X, int Y);

/// <summary>
/// One effect in the compositor chain: alpha-blits the active caption's sprite onto the frame, screen-space
/// (so it sits on top, unaffected by zoom). Holds the per-output-frame resolution (<see cref="CaptionFrame"/>
/// — which cue + its eased alpha) and one sprite per cue; each frame it blits the current cue's sprite at its
/// baked position, scaled by the frame alpha. Pure software raster, headless-testable.
/// </summary>
public sealed class CaptionCompositor : IFrameCompositor
{
    private readonly CaptionFrame[] _frames;
    private readonly IReadOnlyList<CaptionSprite?> _sprites; // indexed by cue

    public CaptionCompositor(CaptionFrame[] frames, IReadOnlyList<CaptionSprite?> spritesByCue)
    {
        _frames = frames;
        _sprites = spritesByCue;
    }

    public void Compose(byte[] bgra, int width, int height, int frameIndex)
    {
        if (_frames.Length == 0) return;
        var f = _frames[Math.Clamp(frameIndex, 0, _frames.Length - 1)];
        if (!f.Active || f.CueIndex < 0 || f.CueIndex >= _sprites.Count) return;
        if (_sprites[f.CueIndex] is not { } sprite) return;
        Blit(bgra, width, height, sprite, f.Alpha);
    }

    // Premultiplied source-over at an offset, the whole sprite scaled by the frame alpha (mirrors
    // CanvasCompositor.BlitStraight, but positioned and bounds-checked rather than full-frame).
    private static void Blit(byte[] bgra, int w, int h, CaptionSprite s, double alpha)
    {
        if (alpha <= 0 || s.Width <= 0 || s.Height <= 0) return;
        if (s.Bgra.Length < s.Width * s.Height * 4) return;

        for (var sy = 0; sy < s.Height; sy++)
        {
            var dy = s.Y + sy;
            if (dy < 0 || dy >= h) continue;
            for (var sx = 0; sx < s.Width; sx++)
            {
                var dx = s.X + sx;
                if (dx < 0 || dx >= w) continue;

                var si = (sy * s.Width + sx) * 4;
                var a = s.Bgra[si + 3] / 255.0 * alpha;
                if (a <= 0) continue;
                var ia = 1 - a;
                var di = (dy * w + dx) * 4;
                bgra[di]     = (byte)Math.Clamp(s.Bgra[si]     * alpha + bgra[di]     * ia, 0, 255);
                bgra[di + 1] = (byte)Math.Clamp(s.Bgra[si + 1] * alpha + bgra[di + 1] * ia, 0, 255);
                bgra[di + 2] = (byte)Math.Clamp(s.Bgra[si + 2] * alpha + bgra[di + 2] * ia, 0, 255);
                bgra[di + 3] = 255;
            }
        }
    }
}
