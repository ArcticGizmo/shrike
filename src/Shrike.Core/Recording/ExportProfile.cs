namespace Shrike.Core.Recording;

/// <summary>The output codec/container an export targets.</summary>
public enum ExportCodec
{
    /// <summary>H.264 in MP4 — plays everywhere, larger files.</summary>
    H264,
    /// <summary>H.265/HEVC in MP4 — smallest, but inline preview needs the HEVC extension.</summary>
    H265,
    /// <summary>Stream-copy the source (no re-encode) — trim only, fastest, source quality.</summary>
    Copy,
    /// <summary>Animated GIF — universally embeddable, large; palette-limited.</summary>
    Gif,
    /// <summary>Animated WebP — tiny, inline-shareable; not previewable everywhere.</summary>
    WebP,
}

/// <summary>
/// A named export target: the footprint dial. Re-encodes the kept ranges of a <see cref="RecordingSource"/>
/// into a small, shareable file. <see cref="MaxHeight"/> caps resolution (downscale only — never upscales),
/// <see cref="FpsCap"/> caps frame rate, and <see cref="Crf"/> is the quality knob for the H.264/H.265
/// encoders (lower = better/bigger). All fields are user-overridable in the export dialog; the presets are
/// just sensible starting points.
/// </summary>
public sealed record ExportProfile(
    string Name,
    ExportCodec Codec,
    int? MaxHeight,
    int? FpsCap,
    int Crf,
    string Note)
{
    /// <summary>File extension (incl. dot) for this codec's container.</summary>
    public string Extension => Codec switch
    {
        ExportCodec.Gif => ".gif",
        ExportCodec.WebP => ".webp",
        _ => ".mp4",
    };

    public bool IsHevc => Codec == ExportCodec.H265;

    /// <summary>The built-in presets, in the order the UI should list them.</summary>
    public static IReadOnlyList<ExportProfile> Presets { get; } = new[]
    {
        new ExportProfile("Slack-small", ExportCodec.H265, MaxHeight: 720, FpsCap: 30, Crf: 30,
            "Smallest file. Needs the HEVC extension to preview inline in Slack/older browsers."),
        new ExportProfile("Balanced", ExportCodec.H265, MaxHeight: 1080, FpsCap: 30, Crf: 28,
            "Small and sharp at 1080p. HEVC — see the compatibility note."),
        new ExportProfile("Most compatible", ExportCodec.H264, MaxHeight: 1080, FpsCap: 30, Crf: 23,
            "Plays everywhere; larger than HEVC."),
        new ExportProfile("Source", ExportCodec.Copy, MaxHeight: null, FpsCap: null, Crf: 0,
            "Trim only, no re-encode — instant, original quality, largest."),
        new ExportProfile("GIF", ExportCodec.Gif, MaxHeight: 480, FpsCap: 15, Crf: 0,
            "Universally embeddable, but large. Best for short clips."),
        new ExportProfile("WebP", ExportCodec.WebP, MaxHeight: 720, FpsCap: 20, Crf: 0,
            "Tiny animated image, inline-shareable. Not previewable everywhere."),
    };

    public static ExportProfile Default => Presets[0];
}
