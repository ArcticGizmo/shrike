namespace Shrike.Core.Audio;

/// <summary>Where a clip's audio came from — this decides its anchoring behaviour and default linking.</summary>
public enum AudioOrigin
{
    /// <summary>Captured live alongside the video. Carries a <see cref="AudioClip.CaptureLink"/> and moves
    /// with its source video region so it never drifts out of lip-sync.</summary>
    LiveCapture,

    /// <summary>Recorded in the editor over the playing preview. Anchored to output time by intent —
    /// it stays where you spoke it relative to what you watched.</summary>
    EditorVoiceover,
}

/// <summary>The span of source-recording time a live-captured clip belongs to. Lets a linked clip ripple
/// with its video when the timeline is cut or reordered (the editor uses it; the model just stores it).</summary>
public readonly record struct SourceSpan(long SourceStartMs, long SourceEndMs)
{
    public long DurationMs => Math.Max(0, SourceEndMs - SourceStartMs);
}

/// <summary>
/// One placed run of audio on the <see cref="AudioTrack"/> — a reference to a sidecar WAV plus where it sits
/// on the output timeline and how it's gained. <b>Anchoring is output-time</b> (see the roadmap decision):
/// <see cref="OutputStartMs"/> is a timeline position. Lip-sync safeguards ride on top — <see cref="AvOffsetMs"/>
/// is a manual ± nudge, and <see cref="CaptureLink"/> ties a live clip back to its video so the editor can
/// keep them together. The clip is immutable; edits produce a new record.
/// </summary>
public sealed record AudioClip
{
    /// <summary>Sidecar WAV path, relative to the recording (like the source MP4 and the mouse-track sidecar).</summary>
    public required string SidecarPath { get; init; }

    /// <summary>Format of the referenced sidecar.</summary>
    public required AudioFormat Format { get; init; }

    /// <summary>Where the clip starts on the output/timeline, in ms (before <see cref="AvOffsetMs"/>).</summary>
    public long OutputStartMs { get; init; }

    /// <summary>How much of the sidecar this clip plays, in ms.</summary>
    public long DurationMs { get; init; }

    /// <summary>Trim-in within the sidecar, in ms — the first <c>SidecarOffsetMs</c> of the file are skipped.</summary>
    public long SidecarOffsetMs { get; init; }

    /// <summary>User gain in decibels (0 = unity). Stored in dB to match the UI; mix uses <see cref="LinearGain"/>.</summary>
    public double GainDb { get; init; }

    /// <summary>Whether the clip is silenced in the mix (kept for quick A/B, not deleted).</summary>
    public bool Muted { get; init; }

    /// <summary>Manual audio/video sync nudge in ms (positive = later). Fixes device latency and residual drift.</summary>
    public long AvOffsetMs { get; init; }

    /// <summary>How this clip's audio was produced.</summary>
    public AudioOrigin Origin { get; init; }

    /// <summary>For a live-captured clip, the source-recording span it was captured over; null otherwise.</summary>
    public SourceSpan? CaptureLink { get; init; }

    /// <summary>Linear gain multiplier from <see cref="GainDb"/> (0 when muted).</summary>
    public double LinearGain => Muted ? 0.0 : Math.Pow(10.0, GainDb / 20.0);

    /// <summary>Output-timeline start after the A/V nudge, clamped to zero.</summary>
    public long EffectiveStartMs => Math.Max(0, OutputStartMs + AvOffsetMs);

    /// <summary>Output-timeline end (exclusive).</summary>
    public long EffectiveEndMs => EffectiveStartMs + Math.Max(0, DurationMs);

    /// <summary>True while this clip covers output time <paramref name="outputMs"/> (half-open [start,end)).</summary>
    public bool CoversOutput(long outputMs) => outputMs >= EffectiveStartMs && outputMs < EffectiveEndMs;

    /// <summary>Razor-split this clip at output time <paramref name="outputMs"/> into two adjacent clips that
    /// together cover the same span and reference the same sidecar (the left keeps the head, the right takes the
    /// tail with its sidecar in-point advanced). Returns null when the point is at or outside the clip, so there
    /// is nothing to split. Gain, mute, A/V offset and origin carry to both halves; a live-capture clip's
    /// <see cref="CaptureLink"/> is likewise split so each half stays tied to its own source span.</summary>
    public (AudioClip Left, AudioClip Right)? SplitAtOutput(long outputMs)
    {
        var d = outputMs - EffectiveStartMs; // ms into the clip at the cut (measured where the user sees it)
        if (d <= 0 || d >= DurationMs) return null;

        var left = this with { DurationMs = d };
        var right = this with
        {
            OutputStartMs = OutputStartMs + d,
            SidecarOffsetMs = SidecarOffsetMs + d,
            DurationMs = DurationMs - d,
            CaptureLink = CaptureLink is { } link
                ? new SourceSpan(link.SourceStartMs + d, link.SourceEndMs)
                : null,
        };
        if (CaptureLink is { } l) left = left with { CaptureLink = new SourceSpan(l.SourceStartMs, l.SourceStartMs + d) };
        return (left, right);
    }
}
