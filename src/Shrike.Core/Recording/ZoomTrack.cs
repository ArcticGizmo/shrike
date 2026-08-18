namespace Shrike.Core.Recording;

/// <summary>
/// One authored zoom on the edited timeline: hold the framing at <see cref="Zoom"/>× centred on
/// (<see cref="CenterX"/>, <see cref="CenterY"/>) — normalised [0..1] focus point — over
/// [<see cref="StartMs"/>, <see cref="EndMs"/>], easing in over <see cref="EaseInMs"/> and out over
/// <see cref="EaseOutMs"/> (the ramp length is the "zoom speed"; the middle is a steady hold). The target is
/// a focus point + factor rather than a raw rect so the crop stays aspect-correct and reuses
/// <see cref="AutoZoom.Viewport"/>; a drag-a-box UI converts a box to this. Only the factor eases — at 1× the
/// viewport is the full frame regardless of centre, so easing 1→Zoom naturally shrinks the crop onto the focus.
/// </summary>
public readonly record struct ZoomEvent(
    long StartMs, long EndMs,
    double CenterX, double CenterY,
    double Zoom,
    long EaseInMs, long EaseOutMs)
{
    public long DurationMs => Math.Max(0, EndMs - StartMs);

    /// <summary>The eased magnification (1..Zoom) this event contributes at edited time <paramref name="tMs"/>;
    /// 1 (no zoom) outside its span. Ease-in/out are clamped to fit the span (they scale down to a triangle
    /// rather than overlap).</summary>
    public double ZoomAt(long tMs)
    {
        if (tMs <= StartMs || tMs >= EndMs || Zoom <= 1) return 1.0;
        double dur = DurationMs;
        double ein = Math.Clamp(EaseInMs, 0, dur);
        double eout = Math.Clamp(EaseOutMs, 0, dur);
        if (ein + eout > dur && ein + eout > 0) { var k = dur / (ein + eout); ein *= k; eout *= k; }

        double local = tMs - StartMs;
        double ramp;
        if (ein > 0 && local < ein) ramp = Smoothstep(local / ein);
        else if (eout > 0 && local > dur - eout) ramp = Smoothstep((dur - local) / eout);
        else ramp = 1.0;
        return 1.0 + (Zoom - 1.0) * ramp;
    }

    private static double Smoothstep(double x)
    {
        x = Math.Clamp(x, 0.0, 1.0);
        return x * x * (3 - 2 * x);
    }
}

/// <summary>
/// An authored zoom track: an ordered set of <see cref="ZoomEvent"/>s the user places on the timeline. Pure
/// and deterministic — <see cref="Resolve"/> turns it into the per-output-frame <see cref="ZoomViewport"/>[]
/// the compositor chain consumes (via <see cref="ZoomCompositor"/> for the transform and
/// <see cref="CursorCompositor"/> for the overlay mapping), exactly the shape <see cref="AutoZoom.Viewports"/>
/// produces from clicks — so authored and auto zoom feed the identical downstream path. Where events overlap,
/// the one with the greater magnification at that frame wins (the UI keeps events apart; this just stays
/// deterministic if they don't).
/// </summary>
public sealed class ZoomTrack
{
    public IReadOnlyList<ZoomEvent> Events { get; }

    public ZoomTrack(IReadOnlyList<ZoomEvent> events)
        => Events = events.OrderBy(e => e.StartMs).ToList();

    public static ZoomTrack Empty { get; } = new(Array.Empty<ZoomEvent>());

    public bool IsEmpty => Events.Count == 0;

    /// <summary>One <see cref="ZoomViewport"/> per output frame for a <paramref name="width"/>×<paramref name="height"/>
    /// export at <paramref name="fps"/>. A frame with no active zoom gets the full-frame viewport (a no-op).</summary>
    public ZoomViewport[] Resolve(int frameCount, int fps, int width, int height)
    {
        var vps = new ZoomViewport[Math.Max(0, frameCount)];
        for (var i = 0; i < vps.Length; i++)
        {
            var tMs = fps > 0 ? (long)(i * 1000.0 / fps) : 0;

            // Pick the event contributing the most zoom at this frame (deterministic on overlap).
            double bestZoom = 1.0; double cx = 0.5, cy = 0.5;
            foreach (var e in Events)
            {
                var z = e.ZoomAt(tMs);
                if (z > bestZoom) { bestZoom = z; cx = e.CenterX; cy = e.CenterY; }
            }

            vps[i] = bestZoom > 1.0001
                ? AutoZoom.Viewport(bestZoom, cx * width, cy * height, width, height)
                : new ZoomViewport(0, 0, width, height);
        }
        return vps;
    }
}
