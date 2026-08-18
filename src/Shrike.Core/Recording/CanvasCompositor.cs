namespace Shrike.Core.Recording;

/// <summary>
/// One effect in the compositor chain: alpha-blits a pre-rendered annotation <b>layer sprite</b> onto the
/// frame while a <see cref="CanvasEffect"/> is active, under a per-frame <see cref="LayerTransform"/>
/// (keyframed move / scale / rotate / opacity). The sprite is a full-frame (w×h) <b>premultiplied</b> BGRA
/// image produced once by the UI-side annotation rasteriser (text/arrows/redaction all free); this compositor
/// just places it. A static layer (identity geometry) takes a cheap straight blit; an animated one takes a
/// bilinear affine resample (inverse-mapped, rotation/scale about the frame centre). Content-space vs
/// screen-space is decided by <b>where</b> this sits in the chain. Pure software raster, headless-testable.
/// </summary>
public sealed class CanvasCompositor : IFrameCompositor
{
    private readonly byte[] _sprite;      // premultiplied BGRA, top-down, width*height*4
    private readonly int _w, _h;
    private readonly LayerTransform[] _transforms;

    public CanvasCompositor(byte[] premultipliedBgraSprite, int width, int height, LayerTransform[] transforms)
    {
        _sprite = premultipliedBgraSprite;
        _w = width;
        _h = height;
        _transforms = transforms;
    }

    public void Compose(byte[] bgra, int width, int height, int frameIndex)
    {
        if (_transforms.Length == 0 || width != _w || height != _h) return;
        var t = _transforms[Math.Clamp(frameIndex, 0, _transforms.Length - 1)];
        if (t.Opacity <= 0) return;

        if (t.IsIdentityGeometry) BlitStraight(bgra, t.Opacity);
        else BlitAffine(bgra, width, height, t);
    }

    // Static layer: 1:1 premultiplied source-over, the whole layer scaled by opacity.
    private void BlitStraight(byte[] bgra, double a)
    {
        var n = Math.Min(_sprite.Length, bgra.Length);
        for (var i = 0; i + 3 < n; i += 4)
        {
            var sa = _sprite[i + 3] / 255.0 * a;
            if (sa <= 0) continue;
            var ia = 1 - sa;
            bgra[i]     = (byte)Math.Clamp(_sprite[i]     * a + bgra[i]     * ia, 0, 255);
            bgra[i + 1] = (byte)Math.Clamp(_sprite[i + 1] * a + bgra[i + 1] * ia, 0, 255);
            bgra[i + 2] = (byte)Math.Clamp(_sprite[i + 2] * a + bgra[i + 2] * ia, 0, 255);
            bgra[i + 3] = 255;
        }
    }

    // Animated layer: for each destination pixel, inverse-map through the transform (about the frame centre)
    // and bilinearly sample the premultiplied sprite. dest = C + T + R(θ)·S·(src − C).
    private void BlitAffine(byte[] bgra, int w, int h, LayerTransform t)
    {
        double cx = w / 2.0, cy = h / 2.0;
        var rad = t.RotationDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        var invS = 1.0 / (Math.Abs(t.Scale) < 1e-6 ? 1e-6 : t.Scale);

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                // Inverse transform: src = C + R(−θ)·((dest − C − T) / S)
                var px = (x - cx - t.Dx) * invS;
                var py = (y - cy - t.Dy) * invS;
                var sx = cx + (px * cos + py * sin);
                var sy = cy + (-px * sin + py * cos);
                if (sx < 0 || sy < 0 || sx > w - 1 || sy > h - 1) continue;

                var (b, g, r, sa) = SampleBilinear(sx, sy, w, h);
                var a = sa / 255.0 * t.Opacity;
                if (a <= 0) continue;
                var idx = (y * w + x) * 4;
                var ia = 1 - a;
                // Sprite is premultiplied, so its own alpha is already baked into (b,g,r); scale by opacity only.
                bgra[idx]     = (byte)Math.Clamp(b * t.Opacity + bgra[idx]     * ia, 0, 255);
                bgra[idx + 1] = (byte)Math.Clamp(g * t.Opacity + bgra[idx + 1] * ia, 0, 255);
                bgra[idx + 2] = (byte)Math.Clamp(r * t.Opacity + bgra[idx + 2] * ia, 0, 255);
                bgra[idx + 3] = 255;
            }
        }
    }

    private (double B, double G, double R, double A) SampleBilinear(double x, double y, int w, int h)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        double fx = x - x0, fy = y - y0;

        (double, double, double, double) P(int px, int py)
        {
            var i = (py * w + px) * 4;
            return (_sprite[i], _sprite[i + 1], _sprite[i + 2], _sprite[i + 3]);
        }
        var (b00, g00, r00, a00) = P(x0, y0);
        var (b10, g10, r10, a10) = P(x1, y0);
        var (b01, g01, r01, a01) = P(x0, y1);
        var (b11, g11, r11, a11) = P(x1, y1);

        double L(double a, double b, double f) => a + (b - a) * f;
        double Bi(double v00, double v10, double v01, double v11) => L(L(v00, v10, fx), L(v01, v11, fx), fy);
        return (Bi(b00, b10, b01, b11), Bi(g00, g10, g01, g11), Bi(r00, r10, r01, r11), Bi(a00, a10, a01, a11));
    }
}
