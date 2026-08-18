namespace Shrike.Core.Recording;

/// <summary>
/// One effect in the compositor chain: a soft radial glow under the smoothed cursor while a
/// <see cref="SpotlightEffect"/> is active. Like <see cref="CursorCompositor"/> it reads the cursor position
/// from a <see cref="SmoothedCursorTrack"/> (export pixels) and maps it through the shared per-frame
/// <see cref="ZoomViewport"/>, so the glow stays glued to the pointer as the framing zooms. It draws
/// <b>before</b> the cursor in the chain (the glow sits under the arrow). Per-frame colour / alpha / radius are
/// pre-resolved into <see cref="SpotlightFrame"/>[] (see <see cref="EffectTrack.ResolveSpotlight"/>) — inactive
/// frames draw nothing, so a clip with no spotlight span is untouched. Pure software raster, headless-testable.
/// </summary>
public sealed class SpotlightCompositor : IFrameCompositor
{
    private readonly SmoothedCursorTrack _track;
    private readonly SpotlightFrame[] _frames;
    private readonly ZoomViewport[]? _viewports;

    public SpotlightCompositor(SmoothedCursorTrack track, SpotlightFrame[] frames, ZoomViewport[]? viewports = null)
    {
        _track = track;
        _frames = frames;
        _viewports = viewports;
    }

    public void Compose(byte[] bgra, int width, int height, int frameIndex)
    {
        if (_track.IsEmpty || _frames.Length == 0) return;
        var sf = _frames[Math.Clamp(frameIndex, 0, _frames.Length - 1)];
        if (!sf.Active || sf.Alpha <= 0 || sf.RadiusPx <= 0) return;

        var frames = _track.Frames;
        var pos = frames[Math.Clamp(frameIndex, 0, frames.Count - 1)];
        var vp = _viewports is { Length: > 0 } vps
            ? vps[Math.Clamp(frameIndex, 0, vps.Length - 1)]
            : new ZoomViewport(0, 0, width, height);

        var cx = (pos.X - vp.X) * (width / vp.Width);
        var cy = (pos.Y - vp.Y) * (height / vp.Height);
        var radius = sf.RadiusPx;

        var x0 = Math.Max(0, (int)(cx - radius));
        var x1 = Math.Min(width - 1, (int)(cx + radius));
        var y0 = Math.Max(0, (int)(cy - radius));
        var y1 = Math.Min(height - 1, (int)(cy + radius));
        var r2 = radius * radius;

        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var dx = x - cx; var dy = y - cy;
                var d2 = dx * dx + dy * dy;
                if (d2 >= r2) continue;
                var t = 1 - Math.Sqrt(d2) / radius;          // 1 at the centre → 0 at the rim
                var cov = t * t * (3 - 2 * t);               // smoothstep falloff
                var a = cov * sf.Alpha;
                if (a <= 0) continue;
                var idx = (y * width + x) * 4;
                var ia = 1 - a;
                bgra[idx] = (byte)(sf.B * a + bgra[idx] * ia);
                bgra[idx + 1] = (byte)(sf.G * a + bgra[idx + 1] * ia);
                bgra[idx + 2] = (byte)(sf.R * a + bgra[idx + 2] * ia);
                bgra[idx + 3] = 255;
            }
        }
    }
}
