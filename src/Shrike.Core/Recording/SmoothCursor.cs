using Shrike.Core.Capture;

namespace Shrike.Core.Recording;

/// <summary>Smoothing strength for the synthetic cursor. Lower <see cref="MinCutoff"/> = smoother/laggier;
/// <see cref="Beta"/> controls how much it loosens with speed. See <see cref="OneEuroFilter"/>.
///
/// <para>The two raw 1€ params read like a dev tool, so the UI exposes a single 0..1 <see cref="Smoothness"/>
/// knob instead (higher = smoother) and maps it to both via <see cref="FromSmoothness"/>. The mapping is
/// anchored so <see cref="DefaultSmoothness"/> reproduces the shipped <see cref="Default"/> look, keeping
/// export behaviour unchanged for anyone who never touches the slider.</para></summary>
public sealed record CursorSmoothing(double MinCutoff, double Beta, double DCutoff = 1.0)
{
    public static CursorSmoothing Default { get; } = new(MinCutoff: 0.8, Beta: 0.35);

    // Smoothness→params curve endpoints. MinCutoff dominates the feel and moves geometrically (responsive
    // 4.0 → very smooth 0.3); Beta eases down linearly (0.5 → 0.25) so more smoothing also tames overshoot
    // on fast moves. Chosen so FromSmoothness(DefaultSmoothness) ≈ Default (0.8, 0.35).
    private const double MinCutoffResponsive = 4.0;
    private const double MinCutoffSmooth = 0.3;
    private const double BetaResponsive = 0.5;
    private const double BetaSmooth = 0.25;

    /// <summary>The single 0..1 knob that reproduces <see cref="Default"/> — where the UI/settings start.</summary>
    public const double DefaultSmoothness = 0.62;

    /// <summary>Build smoothing params from a single 0..1 knob (0 = responsive/minimal, 1 = very smooth).</summary>
    public static CursorSmoothing FromSmoothness(double smoothness)
    {
        var s = Math.Clamp(smoothness, 0.0, 1.0);
        var minCutoff = MinCutoffResponsive * Math.Pow(MinCutoffSmooth / MinCutoffResponsive, s);
        var beta = BetaResponsive + (BetaSmooth - BetaResponsive) * s;
        return new CursorSmoothing(minCutoff, beta);
    }

    /// <summary>The 0..1 knob position for these params (inverse of <see cref="FromSmoothness"/>, read off the
    /// dominant <see cref="MinCutoff"/> axis) — used to seed the slider from a persisted/Default value.</summary>
    public double Smoothness =>
        Math.Clamp(
            Math.Log(MinCutoff / MinCutoffResponsive) / Math.Log(MinCutoffSmooth / MinCutoffResponsive),
            0.0, 1.0);
}

/// <summary>Maps a captured pointer position into the exported frame's pixel space.</summary>
public static class CursorMapping
{
    /// <summary>
    /// Map a virtual-screen physical-pixel point into export-space pixels for a recording of
    /// <paramref name="region"/> exported at <paramref name="exportWidth"/> × <paramref name="exportHeight"/>.
    /// Subtracting the region origin handles any monitor offset; the ratio handles an export downscale.
    /// </summary>
    public static (double X, double Y) ToExport(double px, double py, PixelBounds region, int exportWidth, int exportHeight)
    {
        var sx = region.Width == 0 ? 1.0 : exportWidth / (double)region.Width;
        var sy = region.Height == 0 ? 1.0 : exportHeight / (double)region.Height;
        return ((px - region.X) * sx, (py - region.Y) * sy);
    }
}

/// <summary>A smoothed cursor position for one output frame, in export-space pixels.</summary>
public readonly record struct CursorSample(double X, double Y);

/// <summary>A click landing on the edited timeline: which output frame it belongs to, and which button.</summary>
public readonly record struct CursorClickMark(int FrameIndex, MouseButtonKind Button);

/// <summary>The projected result: one <see cref="CursorSample"/> per output frame, plus click marks.</summary>
public sealed class SmoothedCursorTrack
{
    public int Fps { get; }
    public IReadOnlyList<CursorSample> Frames { get; }
    public IReadOnlyList<CursorClickMark> Clicks { get; }

    public SmoothedCursorTrack(int fps, IReadOnlyList<CursorSample> frames, IReadOnlyList<CursorClickMark> clicks)
    {
        Fps = fps;
        Frames = frames;
        Clicks = clicks;
    }

    /// <summary>True when there's no cursor to draw (an empty track).</summary>
    public bool IsEmpty => Frames.Count == 0;
}

/// <summary>
/// Turns a raw <see cref="MouseTrack"/> into a per-output-frame smoothed cursor in export pixels. The
/// pipeline is: map each raw point into export space, smooth per-axis with the <see cref="OneEuroFilter"/>
/// in source-time order, then resample onto the edited frame grid through the <see cref="Timeline"/>. Because
/// the smoothing happens in continuous source time and each frame samples its own source time, a cut makes
/// the cursor jump (like the video) rather than gliding across removed content. Clicks map to edited frames
/// and are dropped if they fall inside a cut. Pure and headless — no pixels are drawn here (that's SC4).
/// </summary>
public static class SmoothCursor
{
    public static SmoothedCursorTrack Project(
        MouseTrack track, Timeline timeline, int fps, int exportWidth, int exportHeight, CursorSmoothing? smoothing = null)
    {
        smoothing ??= CursorSmoothing.Default;
        var pts = track.Points;
        if (pts.Count == 0 || fps <= 0)
            return new SmoothedCursorTrack(fps, Array.Empty<CursorSample>(), Array.Empty<CursorClickMark>());

        // 1) Map raw points into export space, then smooth each axis (in source-time order).
        var fx = new OneEuroFilter(smoothing.MinCutoff, smoothing.Beta, smoothing.DCutoff);
        var fy = new OneEuroFilter(smoothing.MinCutoff, smoothing.Beta, smoothing.DCutoff);
        var n = pts.Count;
        var srcMs = new double[n];
        var sx = new double[n];
        var sy = new double[n];
        for (var i = 0; i < n; i++)
        {
            var (ex, ey) = CursorMapping.ToExport(pts[i].X, pts[i].Y, track.Region, exportWidth, exportHeight);
            var ts = pts[i].TMs / 1000.0;
            srcMs[i] = pts[i].TMs;
            sx[i] = fx.Filter(ex, ts);
            sy[i] = fy.Filter(ey, ts);
        }

        // 2) Resample onto the edited frame grid — each frame samples the smoothed signal at its source time.
        var frameCount = Math.Max(1, (int)Math.Round(timeline.KeptDurationMs / 1000.0 * fps));
        var frames = new CursorSample[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            var editedMs = (long)Math.Round(i * 1000.0 / fps);
            double source = timeline.EditedToSourceMs(editedMs);
            frames[i] = new CursorSample(Sample(srcMs, sx, source), Sample(srcMs, sy, source));
        }

        // 3) Project click-downs onto edited frames; drop any that fall inside a cut span.
        var clicks = new List<CursorClickMark>();
        foreach (var c in track.Clicks)
        {
            if (!c.Down) continue;
            if (timeline.SourceToEditedMs(c.TMs) is not { } editedMs) continue;
            var idx = Math.Clamp((int)Math.Round(editedMs * fps / 1000.0), 0, frameCount - 1);
            clicks.Add(new CursorClickMark(idx, c.Button));
        }

        return new SmoothedCursorTrack(fps, frames, clicks);
    }

    /// <summary>Linear-interpolate the samples <c>(t, v)</c> (t ascending) at time <paramref name="at"/>, clamped to the ends.</summary>
    private static double Sample(double[] t, double[] v, double at)
    {
        var n = t.Length;
        if (at <= t[0]) return v[0];
        if (at >= t[n - 1]) return v[n - 1];

        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (t[mid] <= at) lo = mid; else hi = mid;
        }
        var span = t[hi] - t[lo];
        if (span <= 0) return v[lo];
        return v[lo] + (v[hi] - v[lo]) * ((at - t[lo]) / span);
    }
}
