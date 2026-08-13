using System.Globalization;
using System.Text;
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
        string outputPath)
    {
        if (keptRanges.Count == 0)
            throw new ArgumentException("Nothing to export — no kept ranges.", nameof(keptRanges));

        var targetH = EvenClampDown(profile.MaxHeight ?? source.Height, source.Height);
        var targetW = targetH == source.Height
            ? source.Width
            : Even((int)Math.Round(source.Width * (double)targetH / source.Height));
        var targetFps = Math.Min(profile.FpsCap ?? source.Fps, source.Fps);
        var scaleNeeded = targetH < source.Height;
        var fpsNeeded = targetFps < source.Fps;

        // Stream-copy is only correct for a single range; anything else must decode.
        if (profile.Codec == ExportCodec.Copy && keptRanges.Count == 1)
            return StreamCopy(source, keptRanges[0], outputPath);

        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", source.Path };

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
                args.AddRange(new[] { "-filter_complex", string.Join(";", chains), "-map", map });
                args.AddRange(VideoCodecArgs(profile, hardware));
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
    private static int Even(int n) => n & ~1;
    private static int EvenClampDown(int desired, int ceiling) => Even(Math.Min(desired, ceiling));
}
