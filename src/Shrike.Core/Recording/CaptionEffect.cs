namespace Shrike.Core.Recording;

/// <summary>
/// One caption line — a span of <b>source</b> time and the text shown across it. Stored in source time
/// (like every <see cref="EffectEvent"/>) so cues ride cuts/trims for free through the timeline's
/// edited↔source mapping — which is exactly the "linked to the source audio" behaviour, since the mic
/// sidecar shares the source (pause-excluded capture) time axis with the video. Pure and UI-free.
/// </summary>
public sealed record CaptionCue(long StartMs, long EndMs, string Text)
{
    public long DurationMs => Math.Max(0, EndMs - StartMs);

    /// <summary>Half-open [Start, End): a cue owns its start frame, not its end (so back-to-back cues never
    /// double up), matching <see cref="EffectEvent.ActiveAt"/>.</summary>
    public bool ActiveAt(long tMs) => tMs >= StartMs && tMs < EndMs;
}

/// <summary>Where a caption sits on the frame.</summary>
public enum CaptionPosition
{
    /// <summary>Lower third — the usual subtitle placement.</summary>
    Bottom,
    /// <summary>Upper third — for when the action is at the bottom of the frame.</summary>
    Top,
}

/// <summary>
/// How captions look — one style per <see cref="CaptionEffect"/> (per-cue overrides are a later refinement).
/// Sizes are resolution-independent: <see cref="FontScale"/> multiplies a height-derived base so text reads
/// consistently at 480p and 4K (mirroring <c>CursorStyle.ForExport</c>). Colours are "#RRGGBB". A translucent
/// background box keeps text legible over busy frames — the default, on purpose.
/// </summary>
public sealed record CaptionStyle(
    double FontScale,
    string TextColor,
    string BoxColor,
    double BoxOpacity,
    CaptionPosition Position,
    double MaxWidthFraction,
    long FadeMs)
{
    /// <summary>Legible-by-default: white text on a ~55%-black box, lower third, wrapping at 80% width, with a
    /// short crossfade between cues.</summary>
    public static CaptionStyle Default { get; } =
        new(1.0, "#FFFFFF", "#000000", 0.55, CaptionPosition.Bottom, 0.80, 80);
}

/// <summary>
/// A captions effect — the whole set of timed <see cref="CaptionCue"/>s for a clip plus their shared
/// <see cref="CaptionStyle"/>, carried as one lane block (like <see cref="CanvasEffect"/> carries an
/// annotation list) rather than one block per cue: that keeps the effects lane uncluttered and matches how
/// Whisper hands back a list of timed segments. <see cref="EffectEvent.StartMs"/>/<see cref="EffectEvent.EndMs"/>
/// span all cues; the per-cue crossfade is <see cref="CaptionStyle.FadeMs"/>, resolved by
/// <see cref="EffectTrack.ResolveCaptions"/>. Captions render screen-space (after the zoom transform) so they
/// stay pinned and readable regardless of zoom.
/// </summary>
public sealed record CaptionEffect(long StartMs, long EndMs, long EaseInMs, long EaseOutMs)
    : EffectEvent(StartMs, EndMs, EaseInMs, EaseOutMs)
{
    public override EffectKind Kind => EffectKind.Caption;

    /// <summary>The caption lines, in source time. Empty = a placed-but-not-yet-transcribed captions block.</summary>
    public IReadOnlyList<CaptionCue> Cues { get; init; } = Array.Empty<CaptionCue>();

    public CaptionStyle Style { get; init; } = CaptionStyle.Default;

    /// <summary>Build a captions effect that spans the given cues (with an optional style). The effect's own
    /// span is the min start → max end of the cues; an empty set yields a zero-length effect.</summary>
    public static CaptionEffect FromCues(IEnumerable<CaptionCue> cues, CaptionStyle? style = null)
    {
        var list = cues.OrderBy(c => c.StartMs).ThenBy(c => c.EndMs).ToList();
        long start = list.Count > 0 ? list.Min(c => c.StartMs) : 0;
        long end = list.Count > 0 ? list.Max(c => c.EndMs) : 0;
        return new CaptionEffect(start, end, 0, 0) { Cues = list, Style = style ?? CaptionStyle.Default };
    }
}

/// <summary>The resolved caption for one output frame — which cue to draw (index into
/// <see cref="CaptionEffect.Cues"/>, or -1 for none) and its eased 0..1 alpha.</summary>
public readonly record struct CaptionFrame(int CueIndex, double Alpha)
{
    public bool Active => CueIndex >= 0 && Alpha > 0;
    public static CaptionFrame Inactive { get; } = new(-1, 0);
}
