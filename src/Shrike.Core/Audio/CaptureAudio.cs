using Shrike.Core.Recording;

namespace Shrike.Core.Audio;

/// <summary>
/// Turns the authored audio track into the clips the exporter and preview actually play, by mapping
/// <b>live-captured</b> audio through the timeline's kept ranges so it rides the video cuts and stays in
/// lip-sync — the sidecar shares the source (pause-excluded) time axis with the video, so a kept source span
/// <c>[a,b)</c> pulls sidecar <c>[a,b)</c> to the concatenated output position, exactly as the video's
/// trim+concat does. Editor-voiceover clips are authored directly in output time and pass through unchanged.
/// Pure and deterministic; the same mapping feeds export and the preview mix so what you hear matches what
/// you get.
/// </summary>
public static class CaptureAudio
{
    /// <summary>Map one clip onto the output timeline. A live-capture clip is split/placed per kept range;
    /// any other clip is returned as-is.</summary>
    public static IReadOnlyList<AudioClip> RideTimeline(AudioClip clip, IReadOnlyList<Segment> keptRanges)
    {
        if (clip.Origin != AudioOrigin.LiveCapture) return [clip];

        var clipSrcStart = clip.SidecarOffsetMs;
        var clipSrcEnd = clip.SidecarOffsetMs + clip.DurationMs;

        var result = new List<AudioClip>();
        long outStart = 0;
        foreach (var range in keptRanges)
        {
            var rangeDur = range.EndMs - range.StartMs;
            var from = Math.Max(range.StartMs, clipSrcStart);
            var to = Math.Min(range.EndMs, clipSrcEnd);
            if (to > from)
            {
                result.Add(clip with
                {
                    SidecarOffsetMs = from,
                    DurationMs = to - from,
                    OutputStartMs = outStart + (from - range.StartMs),
                    CaptureLink = new SourceSpan(from, to),
                });
            }
            outStart += rangeDur;
        }
        return result;
    }

    /// <summary>Build the track to hand to <c>ExportCommand.Build</c> (and the preview mix): every authored
    /// clip mapped through the cuts. With no cuts (one full-length kept range) a live clip maps back to
    /// itself, so an uncut recording exports its audio verbatim.</summary>
    public static AudioTrack ForOutput(AudioTrack authored, IReadOnlyList<Segment> keptRanges)
    {
        if (authored.IsEmpty) return AudioTrack.Empty;
        var clips = new List<AudioClip>();
        foreach (var clip in authored.Clips)
            clips.AddRange(RideTimeline(clip, keptRanges));
        return new AudioTrack(clips);
    }
}
