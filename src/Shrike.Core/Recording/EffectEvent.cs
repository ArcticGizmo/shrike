namespace Shrike.Core.Recording;

/// <summary>
/// The kind of a timeline <see cref="EffectEvent"/>. Used by the UI (colour, "Add effect ▸" menu, the
/// per-kind properties editor) and to keep persistence forgiving — an unknown kind in a future file is
/// simply skipped rather than failing the load.
/// </summary>
public enum EffectKind
{
    Zoom,
    Spotlight,
    Ripple,
    Visibility,
    Canvas,
}

/// <summary>
/// One timed effect on the unified effects timeline, positioned in <b>source</b> time so it stays pinned to
/// its content across cuts (exactly like <see cref="ZoomEvent"/>). This is the shared base every effect kind
/// extends — zoom, spotlight, click-ripple, mouse-visibility, and (later) the drawing canvas — so a single
/// lane can hold them all and a single track can resolve them. Timing is [<see cref="StartMs"/>,
/// <see cref="EndMs"/>] with an eased envelope over <see cref="EaseInMs"/>/<see cref="EaseOutMs"/>;
/// <see cref="RampAt"/> is the shared 0..1 envelope (kinds that don't ease just pass 0). Pure and UI-free —
/// lives in Core with headless tests.
/// </summary>
public abstract record EffectEvent(long StartMs, long EndMs, long EaseInMs, long EaseOutMs)
{
    /// <summary>Which kind this is — a cheap discriminator for UI and serialisation, without a type-switch.</summary>
    public abstract EffectKind Kind { get; }

    public long DurationMs => Math.Max(0, EndMs - StartMs);

    /// <summary>The eased 0..1 envelope at source time <paramref name="tMs"/> — 0 outside the span and at the
    /// ends, 1 across the hold, smoothstepped through the ease-in/out. The eases are clamped to the span and,
    /// if they'd overlap, scaled to a triangle rather than fighting. This mirrors <see cref="ZoomEvent.RampAt"/>
    /// so every effect fades consistently.</summary>
    public double RampAt(long tMs)
    {
        // Half-open [Start, End): the start frame is included (a hard-cut effect is at full strength there, an
        // eased one smoothsteps up from 0), the end frame is not — so back-to-back effects never double up.
        if (tMs < StartMs || tMs >= EndMs) return 0.0;
        double dur = DurationMs;
        double ein = Math.Clamp(EaseInMs, 0, dur);
        double eout = Math.Clamp(EaseOutMs, 0, dur);
        if (ein + eout > dur && ein + eout > 0) { var k = dur / (ein + eout); ein *= k; eout *= k; }

        double local = tMs - StartMs;
        if (ein > 0 && local < ein) return Smoothstep(local / ein);
        if (eout > 0 && local > dur - eout) return Smoothstep((dur - local) / eout);
        return 1.0;
    }

    /// <summary>Whether this effect spans source time <paramref name="tMs"/> — half-open [Start, End), so the
    /// start frame counts and the end frame belongs to whatever follows.</summary>
    public bool ActiveAt(long tMs) => tMs >= StartMs && tMs < EndMs;

    private protected static double Smoothstep(double x)
    {
        x = Math.Clamp(x, 0.0, 1.0);
        return x * x * (3 - 2 * x);
    }
}

/// <summary>
/// A zoom effect — the timeline representation of an authored zoom. Carries the same target (normalised focus
/// point + factor) and eases as the underlying <see cref="ZoomEvent"/>, and converts to one via
/// <see cref="ToZoomEvent"/> so resolution runs through the existing, battle-tested <see cref="ZoomTrack"/>
/// path unchanged (guaranteeing identical framing).
/// </summary>
public sealed record ZoomEffect(
    long StartMs, long EndMs, long EaseInMs, long EaseOutMs,
    double CenterX, double CenterY, double Zoom)
    : EffectEvent(StartMs, EndMs, EaseInMs, EaseOutMs)
{
    public override EffectKind Kind => EffectKind.Zoom;

    public ZoomEvent ToZoomEvent() => new(StartMs, EndMs, CenterX, CenterY, Zoom, EaseInMs, EaseOutMs);

    public static ZoomEffect FromZoomEvent(ZoomEvent e)
        => new(e.StartMs, e.EndMs, e.EaseInMs, e.EaseOutMs, e.CenterX, e.CenterY, e.Zoom);
}

/// <summary>
/// A spotlight effect — a glowing halo under the smoothed cursor while active. Its behaviour (compositor +
/// preview) lands in a later milestone; this is the model the lane authors and persistence stores.
/// </summary>
public sealed record SpotlightEffect(
    long StartMs, long EndMs, long EaseInMs, long EaseOutMs,
    string Color, double Opacity, int Radius)
    : EffectEvent(StartMs, EndMs, EaseInMs, EaseOutMs)
{
    public override EffectKind Kind => EffectKind.Spotlight;
}

/// <summary>
/// A click-ripple effect — enables the expanding ring on mouse clicks that fall within its span. A gate over a
/// range rather than a faded overlay (individual ripples carry their own short fade), so it does not ease.
/// </summary>
public sealed record RippleEffect(long StartMs, long EndMs)
    : EffectEvent(StartMs, EndMs, 0, 0)
{
    public override EffectKind Kind => EffectKind.Ripple;
}

/// <summary>
/// A mouse-visibility effect — whether the synthetic cursor is drawn within its span. Today's clip-wide
/// "Show cursor" default is represented as a single full-length <see cref="VisibilityEffect"/> the user can
/// shorten, split, or delete; a <c>Visible=false</c> span hides the cursor (and its ripple/spotlight) there.
/// </summary>
public sealed record VisibilityEffect(long StartMs, long EndMs, bool Visible)
    : EffectEvent(StartMs, EndMs, 0, 0)
{
    public override EffectKind Kind => EffectKind.Visibility;
}

/// <summary>Whether a <see cref="CanvasEffect"/>'s drawing is glued to the recorded content (magnifies/moves
/// with a zoom) or floats fixed on the output frame (unaffected by zoom).</summary>
public enum CanvasSpace
{
    /// <summary>Composited before zoom — the drawing rides the content it was drawn over.</summary>
    Content,
    /// <summary>Composited after zoom — a fixed screen-space overlay.</summary>
    Screen,
}

/// <summary>
/// A drawing-canvas effect — a set of screenshot-style <see cref="Shrike.Core.Annotations.Annotation"/>s
/// (rectangles, arrows, text, redaction, …) shown over the frame for its span, in content- or screen-space.
/// Annotations are stored in <b>source-frame image pixels</b> (as everywhere in the annotation model), so the
/// drawing is resolution-independent; the compositor rasterises them to a layer sprite and blits it per frame.
/// </summary>
public sealed record CanvasEffect(
    long StartMs, long EndMs, long EaseInMs, long EaseOutMs, CanvasSpace Space)
    : EffectEvent(StartMs, EndMs, EaseInMs, EaseOutMs)
{
    public override EffectKind Kind => EffectKind.Canvas;

    /// <summary>The drawing, in source-frame image pixels. Empty = an as-yet-undrawn canvas.</summary>
    public IReadOnlyList<Shrike.Core.Annotations.Annotation> Annotations { get; init; }
        = Array.Empty<Shrike.Core.Annotations.Annotation>();

    /// <summary>Keyframed transform (move / scale / rotate / fade) applied to the layer over its span. Identity
    /// (the default) is a static layer — animation is additive over that.</summary>
    public CanvasAnimation Animation { get; init; } = CanvasAnimation.Identity;
}
