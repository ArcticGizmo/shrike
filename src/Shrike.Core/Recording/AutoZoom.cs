namespace Shrike.Core.Recording;

/// <summary>Auto-zoom behaviour. <see cref="MaxZoom"/> is the peak magnification (1 = off); a click holds
/// the zoom for <see cref="HoldSeconds"/>, and transitions ease over <see cref="EaseSeconds"/>.</summary>
public sealed record ZoomConfig(
    bool Enabled = true, double MaxZoom = 1.6, double HoldSeconds = 1.6, double EaseSeconds = 0.6)
{
    public static ZoomConfig Default { get; } = new();
    public static ZoomConfig Off { get; } = new(Enabled: false);
}

/// <summary>A crop rectangle over a frame (export pixels) that, scaled back to the frame size, is the zoom.</summary>
public readonly record struct ZoomViewport(double X, double Y, double Width, double Height);

/// <summary>
/// Derives the auto-zoom framing from click activity — the Screen-Studio move. Each click holds a
/// zoom-in for a while; overlapping holds merge into one sustained zoom, and the on/off steps are eased
/// into smooth ramps so the framing glides in and back out. Pure and deterministic (same track + config →
/// same curve), so it's headless-testable; the per-frame <see cref="ZoomViewport"/> is centred on the
/// (already-smoothed) cursor, so the zoom follows the pointer.
/// </summary>
public static class AutoZoom
{
    /// <summary>Per-frame zoom factor (≥1), eased in/out, driven by the click marks.</summary>
    public static double[] ZoomCurve(IReadOnlyList<CursorClickMark> clicks, int frameCount, int fps, ZoomConfig cfg)
    {
        var z = new double[Math.Max(0, frameCount)];
        for (var i = 0; i < z.Length; i++) z[i] = 1.0;
        if (!cfg.Enabled || cfg.MaxZoom <= 1 || frameCount <= 0 || fps <= 0 || clicks.Count == 0) return z;

        // Each click raises the target to MaxZoom for HoldSeconds; overlapping windows merge naturally.
        var hold = Math.Max(1, (int)Math.Round(cfg.HoldSeconds * fps));
        foreach (var c in clicks)
        {
            var start = Math.Clamp(c.FrameIndex, 0, frameCount - 1);
            var end = Math.Min(frameCount, start + hold);
            for (var f = start; f < end; f++) z[f] = cfg.MaxZoom;
        }

        // Ease the rectangular on/off steps into ramps (two box passes ≈ a smoothstep in and out).
        var ease = Math.Max(1, (int)Math.Round(cfg.EaseSeconds * fps));
        z = BoxSmooth(z, ease);
        z = BoxSmooth(z, ease);
        for (var i = 0; i < z.Length; i++) if (z[i] < 1) z[i] = 1;
        return z;
    }

    /// <summary>
    /// Resolve the per-frame zoom framing shared by the whole compositor chain: one <see cref="ZoomViewport"/>
    /// per output frame, each centred on that frame's (already-smoothed) cursor position. A frame whose zoom is
    /// ≈1 gets the full-frame viewport (a no-op for the <see cref="ZoomCompositor"/>). This is the "per-frame
    /// input" the zoom transform and the cursor/ripple overlays both read, so they agree on the framing.
    /// </summary>
    public static ZoomViewport[] Viewports(IReadOnlyList<CursorSample> frames, double[]? zoomCurve, int width, int height)
    {
        var vps = new ZoomViewport[frames.Count];
        for (var i = 0; i < vps.Length; i++)
        {
            var z = zoomCurve is { } zc && i < zc.Length ? zc[i] : 1.0;
            vps[i] = z > 1.0001
                ? Viewport(z, frames[i].X, frames[i].Y, width, height)
                : new ZoomViewport(0, 0, width, height);
        }
        return vps;
    }

    /// <summary>The crop rectangle for a given zoom factor centred on (<paramref name="centerX"/>,
    /// <paramref name="centerY"/>), clamped to stay within the frame.</summary>
    public static ZoomViewport Viewport(double zoom, double centerX, double centerY, int width, int height)
    {
        var z = Math.Max(1, zoom);
        var vw = width / z;
        var vh = height / z;
        var x = Math.Clamp(centerX - vw / 2, 0, width - vw);
        var y = Math.Clamp(centerY - vh / 2, 0, height - vh);
        return new ZoomViewport(x, y, vw, vh);
    }

    // Clamp-at-edges box average; O(n·window) which is trivial for a few thousand frames.
    private static double[] BoxSmooth(double[] v, int radius)
    {
        if (radius <= 0 || v.Length == 0) return v;
        var outp = new double[v.Length];
        for (var i = 0; i < v.Length; i++)
        {
            double sum = 0;
            var n = 0;
            for (var k = i - radius; k <= i + radius; k++)
            {
                sum += v[Math.Clamp(k, 0, v.Length - 1)];
                n++;
            }
            outp[i] = sum / n;
        }
        return outp;
    }
}
