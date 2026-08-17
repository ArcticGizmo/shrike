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
/// The SC4 payoff: an <see cref="IFrameCompositor"/> that draws the smoothed synthetic cursor — and an
/// expanding ripple on each click — onto export frames, from a <see cref="SmoothedCursorTrack"/> (positions
/// already in export pixels). Pure software raster (no UI deps, headless-testable): an anti-aliased arrow
/// sprite is baked once and alpha-blended each frame (dark outline under a light fill); ripples are drawn as
/// soft rings anchored where the click happened.
/// </summary>
public sealed class CursorCompositor : IFrameCompositor
{
    // Cursor arrow in a ~13×19.4 box; the tip (hotspot) is at (0,0).
    private static readonly (double X, double Y)[] Arrow =
    [
        (0, 0), (0, 17), (4.4, 12.6), (7.4, 19.4), (10.2, 18.1), (7.2, 11.4), (13, 11),
    ];

    // BGR components of the palette.
    private static readonly (byte B, byte G, byte R) Fill = (0xEC, 0xF6, 0xFB);   // near-white
    private static readonly (byte B, byte G, byte R) Outline = (0x0D, 0x11, 0x14); // near-black
    private static readonly (byte B, byte G, byte R) Ripple = (0x24, 0xA5, 0xF5);  // amber

    private static readonly (int Dx, int Dy)[] OutlineOffsets =
        [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)];

    private readonly SmoothedCursorTrack _track;
    private readonly CursorStyle _style;
    private readonly byte[] _mask;   // arrow coverage, 0..255
    private readonly int _mw, _mh;

    public CursorCompositor(SmoothedCursorTrack track, CursorStyle? style = null)
    {
        _track = track;
        _style = style ?? CursorStyle.Default;
        _mask = BakeArrow(_style.Height, out _mw, out _mh);
    }

    public void Compose(byte[] bgra, int width, int height, int frameIndex)
    {
        if (_track.IsEmpty) return;
        var frames = _track.Frames;
        var pos = frames[Math.Clamp(frameIndex, 0, frames.Count - 1)];

        // Ripples first (they sit under the cursor), anchored where the click landed.
        var rippleFrames = Math.Max(1, (int)Math.Round(_style.RippleSeconds * _track.Fps));
        foreach (var click in _track.Clicks)
        {
            var age = frameIndex - click.FrameIndex;
            if (age < 0 || age >= rippleFrames) continue;
            var p = age / (double)rippleFrames;
            var c = frames[Math.Clamp(click.FrameIndex, 0, frames.Count - 1)];
            var radius = _style.RippleStartRadius + p * (_style.RippleEndRadius - _style.RippleStartRadius);
            DrawRing(bgra, width, height, c.X, c.Y, radius, _style.RippleThickness, Ripple, (1 - p) * _style.RipplePeakAlpha);
        }

        // Cursor: a dark outline (mask blitted at 1px offsets) then the light fill on top.
        var ax = (int)Math.Round(pos.X);
        var ay = (int)Math.Round(pos.Y);
        foreach (var (dx, dy) in OutlineOffsets)
            Blit(bgra, width, height, ax + dx, ay + dy, Outline, 1.0);
        Blit(bgra, width, height, ax, ay, Fill, 1.0);
    }

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
