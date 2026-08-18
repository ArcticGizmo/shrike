namespace Shrike.Core.Recording;

/// <summary>
/// The unified authored effects for one clip — an ordered set of <see cref="EffectEvent"/>s of any kind
/// (zoom, spotlight, ripple, visibility, canvas) living on a single timeline. This is the model the effects
/// lane edits and the compositor chain draws from; it supersedes the standalone <see cref="ZoomTrack"/> as the
/// authored container, while <b>reusing</b> that track's proven resolver for framing (<see cref="ResolveZoom"/>
/// converts the zoom effects and delegates), so authored zoom stays byte-for-byte identical. Pure,
/// deterministic, UI-free — Core with headless tests. Events are kept ordered by start time for stable,
/// predictable stacking and resolution.
/// </summary>
public sealed class EffectTrack
{
    public IReadOnlyList<EffectEvent> Events { get; }

    public EffectTrack(IEnumerable<EffectEvent> events)
        => Events = events.OrderBy(e => e.StartMs).ThenBy(e => e.EndMs).ToList();

    public static EffectTrack Empty { get; } = new(Array.Empty<EffectEvent>());

    public bool IsEmpty => Events.Count == 0;

    /// <summary>The events of one kind, in track order — the typed view resolvers and per-kind editors use.</summary>
    public IEnumerable<T> OfKind<T>() where T : EffectEvent => Events.OfType<T>();

    /// <summary>The zoom effects as a <see cref="ZoomTrack"/>, so framing resolves through the existing path.</summary>
    public ZoomTrack ZoomTrack => new(OfKind<ZoomEffect>().Select(z => z.ToZoomEvent()).ToList());

    /// <summary>One <see cref="ZoomViewport"/> per output frame — identical to <see cref="ZoomTrack.Resolve"/>,
    /// resolving only the zoom effects. A clip with no zoom effects yields full-frame viewports (a no-op), so
    /// the export path is unchanged when nothing is authored.</summary>
    public ZoomViewport[] ResolveZoom(Timeline timeline, int frameCount, int fps, int width, int height)
        => ZoomTrack.Resolve(timeline, frameCount, fps, width, height);

    /// <summary>The visibility effect covering source time <paramref name="sourceMs"/>, if any (last one wins on
    /// overlap, matching lane draw order). Null when no visibility effect applies there.</summary>
    public VisibilityEffect? VisibilityAt(long sourceMs)
    {
        VisibilityEffect? hit = null;
        foreach (var v in OfKind<VisibilityEffect>())
            if (v.ActiveAt(sourceMs)) hit = v;
        return hit;
    }

    /// <summary>Whether click ripples are enabled at source time <paramref name="sourceMs"/> — true when any
    /// <see cref="RippleEffect"/> spans it.</summary>
    public bool RipplesEnabledAt(long sourceMs)
        => OfKind<RippleEffect>().Any(r => r.ActiveAt(sourceMs));

    public EffectTrack With(IEnumerable<EffectEvent> events) => new(events);
}
