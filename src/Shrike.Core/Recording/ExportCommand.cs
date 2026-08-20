using System.Globalization;
using System.Text;
using Shrike.Core.Audio;
using static Shrike.Core.Recording.HardwareEncoders;

namespace Shrike.Core.Recording;

/// <summary>
/// The ffmpeg invocation that turns a <see cref="RecordingSource"/> + a timeline's kept ranges + an
/// <see cref="ExportProfile"/> into the final file. Pure and side-effect-free: <see cref="Build"/> just
/// assembles the argument list (as discrete args, so filter graphs and paths never get shell-quoted),
/// which makes the whole encode plan headless-testable. <see cref="VideoExporter"/> runs it.
///
/// <para>Kept ranges become <c>trim</c>+<c>concat</c> in a filter graph (frame-accurate); <c>scale</c>
/// downscales (never up) and <c>fps</c> caps the rate. The <em>Source</em> preset stream-copies a single
/// range (no re-encode, instant); a multi-range Source falls back to a near-lossless re-encode since you
/// can't losslessly concat arbitrary cut points.</para>
/// </summary>
public sealed record ExportCommand(
    IReadOnlyList<string> Arguments,
    bool IsReencode,
    int TargetWidth,
    int TargetHeight,
    int TargetFps)
{
    public static ExportCommand Build(
        RecordingSource source,
        IReadOnlyList<Segment> keptRanges,
        ExportProfile profile,
        HwEncoder? hardware,
        string outputPath,
        AudioTrack? audio = null)
    {
        if (keptRanges.Count == 0)
            throw new ArgumentException("Nothing to export — no kept ranges.", nameof(keptRanges));

        // Audio is output-time anchored, so clips place directly on the exported timeline. Muted/empty clips
        // carry no signal; GIF/WebP are silent formats. With no audible audio the whole path below is
        // untouched — "off means off".
        var audioClips = audio is null
            ? []
            : audio.Clips.Where(c => !c.Muted && c.DurationMs > 0).ToArray();
        var hasAudio = audioClips.Length > 0 && SupportsAudio(profile);

        var targetH = EvenClampDown(profile.MaxHeight ?? source.Height, source.Height);
        var targetW = targetH == source.Height
            ? source.Width
            : Even((int)Math.Round(source.Width * (double)targetH / source.Height));
        var targetFps = Math.Min(profile.FpsCap ?? source.Fps, source.Fps);
        var scaleNeeded = targetH < source.Height;
        var fpsNeeded = targetFps < source.Fps;

        // Stream-copy is only correct for a single range with nothing to mux; audio forces a re-encode so the
        // narration can be muxed (the recorded source itself is silent).
        if (profile.Codec == ExportCodec.Copy && keptRanges.Count == 1 && !hasAudio)
            return StreamCopy(source, keptRanges[0], outputPath);

        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", source.Path };
        if (hasAudio)
            foreach (var clip in audioClips) { args.Add("-i"); args.Add(clip.SidecarPath); }

        var chains = new List<string>();
        var body = TrimConcat(keptRanges, chains);

        switch (profile.Codec)
        {
            case ExportCodec.Gif:
                BuildGif(chains, body, targetH, targetFps, scaleNeeded, fpsNeeded);
                args.AddRange(new[] { "-filter_complex", string.Join(";", chains), "-map", "[vout]" });
                break;

            case ExportCodec.WebP:
                AddScaleFps(chains, body, "[vout]", targetH, targetFps, scaleNeeded, fpsNeeded);
                args.AddRange(new[] { "-filter_complex", string.Join(";", chains), "-map", "[vout]" });
                args.AddRange(new[] { "-c:v", "libwebp", "-loop", "0", "-q:v", "75", "-an" });
                break;

            default: // H264 / H265 / multi-range Copy fallback
                var map = AddScaleFps(chains, body, "[vout]", targetH, targetFps, scaleNeeded, fpsNeeded);
                var audioMap = hasAudio ? AddAudioMix(audioClips, chains, inputOffset: 1) : null; // video is input 0
                args.AddRange(new[] { "-filter_complex", string.Join(";", chains), "-map", map });
                if (audioMap is not null) { args.Add("-map"); args.Add(audioMap); }
                args.AddRange(VideoCodecArgs(profile, hardware));
                if (audioMap is not null) args.AddRange(AudioCodecArgs());
                args.AddRange(new[] { "-movflags", "+faststart" });
                break;
        }

        args.Add(outputPath);
        return new ExportCommand(args, IsReencode: true, targetW, targetH, targetFps);
    }

    // ---- filter graph ----

    // Emit one trim+setpts chain per kept range, then concat them. Returns the label carrying the result.
    private static string TrimConcat(IReadOnlyList<Segment> ranges, List<string> chains)
    {
        for (var i = 0; i < ranges.Count; i++)
            chains.Add($"[0:v]trim=start={Sec(ranges[i].StartMs)}:end={Sec(ranges[i].EndMs)}," +
                       $"setpts=PTS-STARTPTS[t{i}]");

        if (ranges.Count == 1) return "[t0]";

        var inputs = string.Concat(Enumerable.Range(0, ranges.Count).Select(i => $"[t{i}]"));
        chains.Add($"{inputs}concat=n={ranges.Count}:v=1:a=0[c]");
        return "[c]";
    }

    // Append a scale/fps chain on top of <paramref name="body"/> if either is needed; return the label to map.
    private static string AddScaleFps(List<string> chains, string body, string outLabel,
        int targetH, int targetFps, bool scale, bool fps)
    {
        var filters = ScaleFpsFilters(targetH, targetFps, scale, fps);
        if (filters.Count == 0) return body;   // nothing to do — map the concat/trim output directly
        chains.Add($"{body}{string.Join(",", filters)}{outLabel}");
        return outLabel;
    }

    // GIF needs a palette: fps/scale, then split → palettegen → paletteuse, all as one graph tail.
    private static void BuildGif(List<string> chains, string body, int targetH, int targetFps, bool scale, bool fps)
    {
        var pre = ScaleFpsFilters(targetH, targetFps, scale, fps);
        var preStr = pre.Count > 0 ? string.Join(",", pre) + "," : "";
        chains.Add($"{body}{preStr}split[a][b]");
        chains.Add("[a]palettegen=stats_mode=diff[p]");
        chains.Add("[b][p]paletteuse=dither=bayer:bayer_scale=5[vout]");
    }

    private static List<string> ScaleFpsFilters(int targetH, int targetFps, bool scale, bool fps)
    {
        var f = new List<string>();
        if (fps) f.Add($"fps={targetFps}");
        if (scale) f.Add($"scale=-2:{targetH}:flags=lanczos");
        return f;
    }

    // ---- audio graph ----

    // Only the muxable video codecs carry audio; GIF/WebP stay silent.
    private static bool SupportsAudio(ExportProfile profile) =>
        profile.Codec is ExportCodec.H264 or ExportCodec.H265 or ExportCodec.Copy;

    // One chain per clip: trim the used span from its sidecar (input i + inputOffset), reset PTS, delay to the
    // clip's output position, apply gain. Then amix them. Returns the label to map. inputOffset is 1 for the
    // export (video is input 0) and 0 for an audio-only mix.
    private static string AddAudioMix(IReadOnlyList<AudioClip> clips, List<string> chains, int inputOffset)
    {
        for (var i = 0; i < clips.Count; i++)
        {
            var c = clips[i];
            var filters = new List<string>
            {
                $"atrim=start={Sec(c.SidecarOffsetMs)}:end={Sec(c.SidecarOffsetMs + c.DurationMs)}",
                "asetpts=PTS-STARTPTS",
            };
            if (c.EffectiveStartMs > 0) filters.Add($"adelay={c.EffectiveStartMs}:all=1");
            if (Math.Abs(c.LinearGain - 1.0) > 1e-6) filters.Add($"volume={Num(c.LinearGain)}");
            chains.Add($"[{i + inputOffset}:a]{string.Join(",", filters)}[a{i}]");
        }

        if (clips.Count == 1) return "[a0]";

        var labels = string.Concat(Enumerable.Range(0, clips.Count).Select(i => $"[a{i}]"));
        // normalize=0: keep authored gains rather than letting amix attenuate by input count.
        chains.Add($"{labels}amix=inputs={clips.Count}:normalize=0[aout]");
        return "[aout]";
    }

    private static IEnumerable<string> AudioCodecArgs() => new[] { "-c:a", "aac", "-b:a", "160k" };

    /// <summary>Build an <b>audio-only</b> ffmpeg command that mixes <paramref name="audio"/> down to a PCM WAV
    /// on the output timeline — used to pre-render the editor's preview mix (multiple clips + gains, exactly as
    /// the export mixes them). Returns an empty-ish command (no filter/map) when nothing is audible; the caller
    /// checks <see cref="AudioTrack.HasAudibleContent"/> first.</summary>
    public static IReadOnlyList<string> BuildAudioMix(AudioTrack audio, string outputWavPath)
    {
        var clips = audio.Clips.Where(c => !c.Muted && c.DurationMs > 0).ToArray();
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
        foreach (var c in clips) { args.Add("-i"); args.Add(c.SidecarPath); }
        if (clips.Length == 0) return args;

        var chains = new List<string>();
        var map = AddAudioMix(clips, chains, inputOffset: 0); // audio clips are inputs 0..N-1 (no video)
        args.AddRange(new[] { "-filter_complex", string.Join(";", chains), "-map", map, "-c:a", "pcm_s16le", outputWavPath });
        return args;
    }

    // ---- codecs ----

    private static IEnumerable<string> VideoCodecArgs(ExportProfile profile, HwEncoder? hw)
    {
        // Copy fallback for a multi-range Source export: re-encode near-lossless rather than glitch a copy.
        if (profile.Codec == ExportCodec.Copy)
            return new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p" };

        if (hw is not null && hw.Codec == profile.Codec)
        {
            var a = new List<string> { "-c:v", hw.Name };
            a.AddRange(hw.QualityArgs(profile.Crf));
            if (profile.IsHevc) { a.Add("-tag:v"); a.Add("hvc1"); }   // QuickTime/Slack-friendly HEVC tag
            return a;
        }

        return profile.Codec switch
        {
            ExportCodec.H265 => new[]
                { "-c:v", "libx265", "-preset", "fast", "-crf", profile.Crf.ToString(),
                  "-pix_fmt", "yuv420p", "-tag:v", "hvc1" },
            _ => new[]
                { "-c:v", "libx264", "-preset", "veryfast", "-crf", profile.Crf.ToString(),
                  "-pix_fmt", "yuv420p" },
        };
    }

    private static ExportCommand StreamCopy(RecordingSource source, Segment range, string outputPath)
    {
        // Fast keyframe seek: -ss/-to before -i. Rough (lands on the nearest keyframe), which is the
        // documented trade-off for the no-re-encode path.
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-ss", Sec(range.StartMs), "-to", Sec(range.EndMs),
            "-i", source.Path,
            "-c", "copy", "-movflags", "+faststart",
            outputPath,
        };
        return new ExportCommand(args, IsReencode: false, source.Width, source.Height, source.Fps);
    }

    // ---- helpers ----

    private static string Sec(long ms) => (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static int Even(int n) => n & ~1;
    private static int EvenClampDown(int desired, int ceiling) => Even(Math.Min(desired, ceiling));
}
