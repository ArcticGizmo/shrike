namespace Shrike.Core.Recording;

/// <summary>
/// The zoom effect as a frame transform in the compositor chain: for each frame it crops to a per-frame
/// <see cref="ZoomViewport"/> (centred on the cursor, see <see cref="AutoZoom.Viewports"/>) and bilinear-scales
/// that crop back up to the full frame size, in place — so the framing magnifies while output stays the same
/// size. A viewport equal to the full frame is a no-op (no zoom that frame). Runs <em>before</em> overlay
/// effects like <see cref="CursorCompositor"/>, which map their draw positions through the same viewports so
/// they stay glued to the pointer at a constant on-screen size. Pure software raster; headless-testable.
/// </summary>
public sealed class ZoomCompositor : IFrameCompositor
{
    private readonly ZoomViewport[] _viewports;   // one per output frame
    private byte[]? _temp;                         // scratch for the resample (reused across frames)

    public ZoomCompositor(ZoomViewport[] viewports) => _viewports = viewports;

    public void Compose(byte[] bgra, int width, int height, int frameIndex)
    {
        if (_viewports.Length == 0) return;
        var vp = _viewports[Math.Clamp(frameIndex, 0, _viewports.Length - 1)];
        // Full-frame viewport → this frame isn't zoomed; skip the resample entirely.
        if (vp.Width >= width - 0.5 && vp.Height >= height - 0.5) return;
        Resample(bgra, width, height, vp);
    }

    // Crop `bgra` to `vp` and bilinear-scale it back to width×height, in place (via a reused scratch copy).
    private void Resample(byte[] bgra, int w, int h, ZoomViewport vp)
    {
        var need = w * h * 4;
        if (_temp is null || _temp.Length != need) _temp = new byte[need];
        Array.Copy(bgra, _temp, need);

        var sxStep = vp.Width / w;
        var syStep = vp.Height / h;
        for (var oy = 0; oy < h; oy++)
        {
            var srcY = vp.Y + (oy + 0.5) * syStep - 0.5;
            var y0 = (int)Math.Floor(srcY);
            var fy = srcY - y0;
            var y0c = Math.Clamp(y0, 0, h - 1);
            var y1c = Math.Clamp(y0 + 1, 0, h - 1);
            for (var ox = 0; ox < w; ox++)
            {
                var srcX = vp.X + (ox + 0.5) * sxStep - 0.5;
                var x0 = (int)Math.Floor(srcX);
                var fx = srcX - x0;
                var x0c = Math.Clamp(x0, 0, w - 1);
                var x1c = Math.Clamp(x0 + 1, 0, w - 1);

                var i00 = (y0c * w + x0c) * 4;
                var i01 = (y0c * w + x1c) * 4;
                var i10 = (y1c * w + x0c) * 4;
                var i11 = (y1c * w + x1c) * 4;
                var d = (oy * w + ox) * 4;
                for (var ch = 0; ch < 3; ch++)
                {
                    var top = _temp[i00 + ch] * (1 - fx) + _temp[i01 + ch] * fx;
                    var bot = _temp[i10 + ch] * (1 - fx) + _temp[i11 + ch] * fx;
                    bgra[d + ch] = (byte)(top * (1 - fy) + bot * fy + 0.5);
                }
                bgra[d + 3] = 255;
            }
        }
    }
}
