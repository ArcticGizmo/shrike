namespace Shrike.Core.Recording;

/// <summary>
/// One authored zoom, positioned in <b>source</b> time so it stays pinned to its content across cuts: hold
/// the framing at <see cref="Zoom"/>× centred on (<see cref="CenterX"/>, <see cref="CenterY"/>) — normalised
/// [0..1] focus point — over [<see cref="StartMs"/>, <see cref="EndMs"/>], easing in over <see cref="EaseInMs"/>
/// and out over <see cref="EaseOutMs"/> (the ramp length is the "zoom speed"; the middle is a steady hold). The target is
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

    /// <summary>The eased 0..1 ramp this event is at for edited time <paramref name="tMs"/> — 0 outside its
    /// span and at the ends, 1 across the hold, smoothstepped through the ease-in/out (clamped to fit the span,
    /// scaling to a triangle rather than overlapping). This drives a whole-rectangle lerp so every crop edge
    /// moves together (no clamp-induced pan/overshoot).</summary>
    public double RampAt(long tMs)
    {
        if (tMs <= StartMs || tMs >= EndMs || Zoom <= 1) return 0.0;
        double dur = DurationMs;
        double ein = Math.Clamp(EaseInMs, 0, dur);
        double eout = Math.Clamp(EaseOutMs, 0, dur);
        if (ein + eout > dur && ein + eout > 0) { var k = dur / (ein + eout); ein *= k; eout *= k; }

        double local = tMs - StartMs;
        if (ein > 0 && local < ein) return Smoothstep(local / ein);
        if (eout > 0 && local > dur - eout) return Smoothstep((dur - local) / eout);
        return 1.0;
    }

    /// <summary>The eased magnification (1..Zoom) this event contributes at <paramref name="tMs"/>.</summary>
    public double ZoomAt(long tMs) => 1.0 + (Zoom - 1.0) * RampAt(tMs);

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
    /// export at <paramref name="fps"/>. Each output frame's edited time is mapped back to source time through
    /// <paramref name="timeline"/> (so events resolve against the content that plays there, and events inside a
    /// cut simply never show). A frame with no active zoom gets the full-frame viewport (a no-op).</summary>
    public ZoomViewport[] Resolve(Timeline timeline, int frameCount, int fps, int width, int height)
    {
        var vps = new ZoomViewport[Math.Max(0, frameCount)];
        var full = new ZoomViewport(0, 0, width, height);
        for (var i = 0; i < vps.Length; i++)
        {
            var editedMs = fps > 0 ? (long)(i * 1000.0 / fps) : 0;
            var tMs = timeline.EditedToSourceMs(editedMs); // evaluate events in source time

            // Pick the event contributing the most zoom at this frame (deterministic on overlap).
            ZoomEvent? best = null; double bestZoom = 1.0;
            foreach (var e in Events)
            {
                var z = e.ZoomAt(tMs);
                if (z > bestZoom) { bestZoom = z; best = e; }
            }

            // Lerp the whole crop from the full frame to the event's final (clamped) target by its eased ramp —
            // so every edge moves together and the framing never overshoots and slides back.
            if (best is { } ev && bestZoom > 1.0001)
            {
                var target = AutoZoom.Viewport(ev.Zoom, ev.CenterX * width, ev.CenterY * height, width, height);
                vps[i] = Lerp(full, target, ev.RampAt(tMs));
            }
            else
            {
                vps[i] = full;
            }
        }
        return vps;
    }

    private static ZoomViewport Lerp(ZoomViewport a, ZoomViewport b, double t) => new(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        a.Width + (b.Width - a.Width) * t,
        a.Height + (b.Height - a.Height) * t);
}
