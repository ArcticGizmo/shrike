namespace Shrike.Core.Recording;

/// <summary>
/// One effect in the compositor chain: alpha-blits a pre-rendered annotation <b>layer sprite</b> onto the
/// frame while a <see cref="CanvasEffect"/> is active. The sprite is a full-frame (w×h) <b>premultiplied</b>
/// BGRA image produced once by the UI-side annotation rasteriser (so text/arrows/redaction all come for free);
/// this compositor just blits it, per frame, scaled by the effect's eased envelope (pre-resolved into
/// <paramref name="alphaPerFrame"/>). Content-space vs screen-space is decided by <b>where</b> this sits in
/// the chain — a content-space canvas is blitted before <see cref="ZoomCompositor"/> (so zoom magnifies it),
/// a screen-space one after every other overlay (fixed on the output). Pure software raster, headless-testable.
/// </summary>
public sealed class CanvasCompositor : IFrameCompositor
{
    private readonly byte[] _sprite;      // premultiplied BGRA, top-down, width*height*4
    private readonly int _w, _h;
    private readonly double[] _alphaPerFrame;

    public CanvasCompositor(byte[] premultipliedBgraSprite, int width, int height, double[] alphaPerFrame)
    {
        _sprite = premultipliedBgraSprite;
        _w = width;
        _h = height;
        _alphaPerFrame = alphaPerFrame;
    }

    public void Compose(byte[] bgra, int width, int height, int frameIndex)
    {
        if (_alphaPerFrame.Length == 0 || width != _w || height != _h) return;
        var a = _alphaPerFrame[Math.Clamp(frameIndex, 0, _alphaPerFrame.Length - 1)];
        if (a <= 0) return;

        var n = Math.Min(_sprite.Length, bgra.Length);
        for (var i = 0; i + 3 < n; i += 4)
        {
            // Premultiplied source-over, the whole layer further scaled by the eased alpha `a`:
            //   out = src*a + dst*(1 - srcA*a)
            var sa = _sprite[i + 3] / 255.0 * a;
            if (sa <= 0) continue;
            var ia = 1 - sa;
            bgra[i]     = (byte)Math.Clamp(_sprite[i]     * a + bgra[i]     * ia, 0, 255);
            bgra[i + 1] = (byte)Math.Clamp(_sprite[i + 1] * a + bgra[i + 1] * ia, 0, 255);
            bgra[i + 2] = (byte)Math.Clamp(_sprite[i + 2] * a + bgra[i + 2] * ia, 0, 255);
            bgra[i + 3] = 255;
        }
    }
}
