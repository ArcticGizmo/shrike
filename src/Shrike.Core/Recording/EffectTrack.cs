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

    /// <summary>The active spotlight at source time <paramref name="sourceMs"/> (the one with the strongest
    /// eased envelope on overlap), or an inactive frame. <paramref name="height"/> scales the radius to the
    /// export frame so it reads consistently across resolutions.</summary>
    public SpotlightFrame SpotlightAt(long sourceMs, int height)
    {
        SpotlightEffect? best = null; double bestRamp = 0;
        foreach (var s in OfKind<SpotlightEffect>())
        {
            var r = s.RampAt(sourceMs);
            if (r > bestRamp) { bestRamp = r; best = s; }
        }
        if (best is null || bestRamp <= 0) return SpotlightFrame.Inactive;
        var (r8, g8, b8) = ParseHex(best.Color);
        var radiusPx = Math.Clamp(best.Radius * height / 540.0, 12, height / 2.0);
        return new SpotlightFrame(true, Math.Clamp(best.Opacity, 0, 1) * bestRamp, r8, g8, b8, radiusPx);
    }

    // ---- per-output-frame resolvers (mirror ZoomTrack.Resolve: edited frame → source time via the timeline) ----

    private long SourceMsAtFrame(Timeline timeline, int frame, int fps)
        => timeline.EditedToSourceMs(fps > 0 ? (long)(frame * 1000.0 / fps) : 0);

    /// <summary>Per output frame: whether the synthetic cursor is drawn. No visibility effect covering a frame
    /// means shown (the default); a <c>Visible=false</c> span hides it there.</summary>
    public bool[] ResolveCursorVisible(Timeline timeline, int frameCount, int fps)
    {
        var a = new bool[Math.Max(0, frameCount)];
        for (var i = 0; i < a.Length; i++) a[i] = VisibilityAt(SourceMsAtFrame(timeline, i, fps))?.Visible ?? true;
        return a;
    }

    /// <summary>Per output frame: whether click ripples are enabled (a <see cref="RippleEffect"/> spans it).</summary>
    public bool[] ResolveRipplesEnabled(Timeline timeline, int frameCount, int fps)
    {
        var a = new bool[Math.Max(0, frameCount)];
        for (var i = 0; i < a.Length; i++) a[i] = RipplesEnabledAt(SourceMsAtFrame(timeline, i, fps));
        return a;
    }

    /// <summary>Per output frame: the spotlight to draw under the cursor (inactive frames draw nothing).</summary>
    public SpotlightFrame[] ResolveSpotlight(Timeline timeline, int frameCount, int fps, int height)
    {
        var a = new SpotlightFrame[Math.Max(0, frameCount)];
        for (var i = 0; i < a.Length; i++) a[i] = SpotlightAt(SourceMsAtFrame(timeline, i, fps), height);
        return a;
    }

    /// <summary>True when any spotlight is active on at least one frame — lets the caller skip the compositor.</summary>
    public bool HasSpotlight => OfKind<SpotlightEffect>().Any();

    /// <summary>Per output frame: one effect's eased 0..1 envelope (its <see cref="EffectEvent.RampAt"/> sampled
    /// at each frame's source time). Drives a spotlight's intensity.</summary>
    public static double[] ResolveEnvelope(EffectEvent ev, Timeline timeline, int frameCount, int fps)
    {
        var a = new double[Math.Max(0, frameCount)];
        for (var i = 0; i < a.Length; i++)
            a[i] = ev.RampAt(timeline.EditedToSourceMs(fps > 0 ? (long)(i * 1000.0 / fps) : 0));
        return a;
    }

    /// <summary>Per output frame: a canvas layer's full transform — its keyframed move/scale/rotate/opacity,
    /// with the opacity folded together with the effect's own eased envelope (so a static layer with no
    /// animation reduces to identity geometry at the envelope's alpha, i.e. exactly today's behaviour).</summary>
    public static LayerTransform[] ResolveCanvasTransforms(CanvasEffect c, Timeline timeline, int frameCount, int fps)
    {
        var a = new LayerTransform[Math.Max(0, frameCount)];
        for (var i = 0; i < a.Length; i++)
        {
            var srcMs = timeline.EditedToSourceMs(fps > 0 ? (long)(i * 1000.0 / fps) : 0);
            var env = c.RampAt(srcMs);
            var local = Math.Clamp(srcMs - c.StartMs, 0, c.DurationMs);
            var t = c.Animation.SampleAt(local);
            a[i] = t with { Opacity = env * t.Opacity };
        }
        return a;
    }

    public EffectTrack With(IEnumerable<EffectEvent> events) => new(events);

    // Parse "#RRGGBB" / "RRGGBB" (and tolerate "#AARRGGBB"); fall back to the spotlight default amber.
    internal static (byte R, byte G, byte B) ParseHex(string? hex)
    {
        var s = (hex ?? "").TrimStart('#');
        if (s.Length == 8) s = s[2..]; // drop leading alpha
        if (s.Length == 6
            && byte.TryParse(s[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(s[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(s[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
            return (r, g, b);
        return (0xFF, 0xD2, 0x4A);
    }
}

/// <summary>The resolved spotlight for one frame — inactive, or a colour + eased alpha + radius (export px).</summary>
public readonly record struct SpotlightFrame(bool Active, double Alpha, byte R, byte G, byte B, double RadiusPx)
{
    public static SpotlightFrame Inactive { get; } = new(false, 0, 0, 0, 0, 0);
}
