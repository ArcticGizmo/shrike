namespace Shrike.Core.Recording;

/// <summary>Look of the synthetic cursor + click ripple. Sizes are in export pixels.</summary>
public sealed record CursorStyle(
    int Height = 24,
    double RippleSeconds = 0.35,
    double RippleStartRadius = 6,
    double RippleEndRadius = 42,
    double RippleThickness = 2.5,
    double RipplePeakAlpha = 0.5)
{
    public static CursorStyle Default { get; } = new();
}

/// <summary>
/// The SC4/SC5 payoff: an <see cref="IFrameCompositor"/> that draws the smoothed synthetic cursor — and an
/// expanding ripple on each click — onto export frames, and (SC5) applies auto-zoom by cropping+scaling each
/// frame to a per-frame <see cref="ZoomViewport"/>. Positions come from a <see cref="SmoothedCursorTrack"/>
/// (already in export pixels) and are mapped through the viewport, so the cursor stays glued to the pointer
/// while the framing zooms; the cursor and ripples keep a constant on-screen size. Pure software raster (no
/// UI deps, headless-testable): an anti-aliased arrow sprite is baked once, and zoom uses a bilinear resample.
/// </summary>
public sealed class CursorCompositor : IFrameCompositor
{
    // Cursor arrow in a ~13×19.4 box; the tip (hotspot) is at (0,0).
    private static readonly (double X, double Y)[] Arrow =
    [
        (0, 0), (0, 17), (4.4, 12.6), (7.4, 19.4), (10.2, 18.1), (7.2, 11.4), (13, 11),
    ];

    private static readonly (byte B, byte G, byte R) Fill = (0xEC, 0xF6, 0xFB);   // near-white
    private static readonly (byte B, byte G, byte R) Outline = (0x0D, 0x11, 0x14); // near-black
    private static readonly (byte B, byte G, byte R) Ripple = (0x24, 0xA5, 0xF5);  // amber

    private static readonly (int Dx, int Dy)[] OutlineOffsets =
        [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)];

    private readonly SmoothedCursorTrack _track;
    private readonly CursorStyle _style;
    private readonly double[]? _zoom;   // per-frame zoom factor (≥1), or null for no zoom
    private readonly byte[] _mask;      // arrow coverage, 0..255
    private readonly int _mw, _mh;
    private byte[]? _temp;              // scratch for the zoom resample (reused across frames)

    public CursorCompositor(SmoothedCursorTrack track, CursorStyle? style = null, double[]? zoomCurve = null)
    {
        _track = track;
        _style = style ?? CursorStyle.Default;
        _zoom = zoomCurve;
        _mask = BakeArrow(_style.Height, out _mw, out _mh);
    }

    public void Compose(byte[] bgra, int width, int height, int frameIndex)
    {
        if (_track.IsEmpty) return;
        var frames = _track.Frames;
        var pos = frames[Math.Clamp(frameIndex, 0, frames.Count - 1)];

        // Auto-zoom: crop to the viewport (centred on the cursor) and scale back up.
        var z = _zoom is { } zc && frameIndex >= 0 && frameIndex < zc.Length ? zc[frameIndex] : 1.0;
        var vp = z > 1.0001
            ? AutoZoom.Viewport(z, pos.X, pos.Y, width, height)
            : new ZoomViewport(0, 0, width, height);
        if (z > 1.0001) Resample(bgra, width, height, vp);

        // Ripples first (under the cursor), anchored where the click landed — mapped through the viewport.
        var rippleFrames = Math.Max(1, (int)Math.Round(_style.RippleSeconds * _track.Fps));
        foreach (var click in _track.Clicks)
        {
            var age = frameIndex - click.FrameIndex;
            if (age < 0 || age >= rippleFrames) continue;
            var p = age / (double)rippleFrames;
            var c = frames[Math.Clamp(click.FrameIndex, 0, frames.Count - 1)];
            var (rx, ry) = Map(c.X, c.Y, vp, width, height);
            var radius = _style.RippleStartRadius + p * (_style.RippleEndRadius - _style.RippleStartRadius);
            DrawRing(bgra, width, height, rx, ry, radius, _style.RippleThickness, Ripple, (1 - p) * _style.RipplePeakAlpha);
        }

        // Cursor: a dark outline (mask blitted at 1px offsets) then the light fill on top.
        var (mxp, myp) = Map(pos.X, pos.Y, vp, width, height);
        var ax = (int)Math.Round(mxp);
        var ay = (int)Math.Round(myp);
        foreach (var (dx, dy) in OutlineOffsets)
            Blit(bgra, width, height, ax + dx, ay + dy, Outline, 1.0);
        Blit(bgra, width, height, ax, ay, Fill, 1.0);
    }

    // Map a full-frame export point into on-screen (post-zoom) coordinates. Identity when vp is the full frame.
    private static (double X, double Y) Map(double px, double py, ZoomViewport vp, int w, int h)
        => ((px - vp.X) * (w / vp.Width), (py - vp.Y) * (h / vp.Height));

    // ---- raster helpers ----

    private void Blit(byte[] bgra, int w, int h, int atX, int atY, (byte B, byte G, byte R) c, double alphaScale)
    {
        for (var my = 0; my < _mh; my++)
        {
            var ty = atY + my;
            if (ty < 0 || ty >= h) continue;
            for (var mx = 0; mx < _mw; mx++)
            {
                var tx = atX + mx;
                if (tx < 0 || tx >= w) continue;
                var a = _mask[my * _mw + mx] / 255.0 * alphaScale;
                if (a > 0) Blend(bgra, (ty * w + tx) * 4, c, a);
            }
        }
    }

    private static void DrawRing(byte[] bgra, int w, int h, double cx, double cy, double radius, double thickness,
        (byte B, byte G, byte R) c, double alpha)
    {
        if (alpha <= 0) return;
        var x0 = Math.Max(0, (int)(cx - radius - thickness));
        var x1 = Math.Min(w - 1, (int)(cx + radius + thickness));
        var y0 = Math.Max(0, (int)(cy - radius - thickness));
        var y1 = Math.Min(h - 1, (int)(cy + radius + thickness));
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                var cov = 1 - Math.Min(1, Math.Abs(d - radius) / thickness);
                if (cov > 0) Blend(bgra, (y * w + x) * 4, c, cov * alpha);
            }
        }
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

    // Straight src-over onto an opaque BGRA frame.
    private static void Blend(byte[] bgra, int idx, (byte B, byte G, byte R) c, double a)
    {
        if (a > 1) a = 1;
        var ia = 1 - a;
        bgra[idx] = (byte)(c.B * a + bgra[idx] * ia);
        bgra[idx + 1] = (byte)(c.G * a + bgra[idx + 1] * ia);
        bgra[idx + 2] = (byte)(c.R * a + bgra[idx + 2] * ia);
        bgra[idx + 3] = 255;
    }

    // Rasterise the arrow to a coverage mask at the requested height (supersampled point-in-polygon).
    private static byte[] BakeArrow(int height, out int mw, out int mh)
    {
        const double baseHeight = 19.4, baseWidth = 13.0;
        var scale = Math.Max(1, height) / baseHeight;
        mw = (int)Math.Ceiling(baseWidth * scale) + 1;
        mh = (int)Math.Ceiling(baseHeight * scale) + 1;
        var mask = new byte[mw * mh];

        const int ss = 4;
        for (var py = 0; py < mh; py++)
        {
            for (var px = 0; px < mw; px++)
            {
                var inside = 0;
                for (var sy = 0; sy < ss; sy++)
                {
                    for (var sx = 0; sx < ss; sx++)
                    {
                        var fx = (px + (sx + 0.5) / ss) / scale;
                        var fy = (py + (sy + 0.5) / ss) / scale;
                        if (InArrow(fx, fy)) inside++;
                    }
                }
                mask[py * mw + px] = (byte)(inside * 255 / (ss * ss));
            }
        }
        return mask;
    }

    private static bool InArrow(double x, double y)
    {
        var inside = false;
        for (int i = 0, j = Arrow.Length - 1; i < Arrow.Length; j = i++)
        {
            var (xi, yi) = Arrow[i];
            var (xj, yj) = Arrow[j];
            if (yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }
}
