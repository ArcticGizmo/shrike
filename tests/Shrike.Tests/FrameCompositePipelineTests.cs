using System.Diagnostics;
using Shrike.Core.Recording;

namespace Shrike.Tests;

/// <summary>
/// SC3 exit criterion: the decode → compose → encode pass round-trips a recording (identity compositor)
/// to a playable MP4 whose duration/frame-count match the edited (kept) ranges. Synthesises the source
/// with ffmpeg's test pattern, so it needs no screen — just ffmpeg. No-ops when ffmpeg is absent.
/// </summary>
public class FrameCompositePipelineTests
{
    private sealed class CountingCompositor : IFrameCompositor
    {
        public int Count;
        public void Compose(byte[] bgra, int width, int height, int frameIndex) => Count++;
    }

    [Fact]
    public void Identity_round_trip_matches_duration_and_frame_count()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg) return;

        var dir = Path.Combine(Path.GetTempPath(), $"shrike-composite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var srcPath = Path.Combine(dir, "src.mp4");
        var outPath = Path.Combine(dir, "out.mp4");
        try
        {
            // A known 4-second, 320x240, 30fps source → 120 frames.
            Run(ffmpeg, "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc=duration=4:size=320x240:rate=30",
                "-pix_fmt", "yuv420p", srcPath);
            var source = MediaProbe.Probe(ffmpeg, srcPath);
            Assert.NotNull(source);

            var timeline = new Timeline(source!);
            var counter = new CountingCompositor();
            var pipeline = new FrameCompositePipeline(ffmpeg);

            var frames = pipeline.Run(source!, timeline.KeptRanges,
                source!.Width, source.Height, source.Fps, bitrate: 2_000_000, outPath, counter);

            Assert.InRange(frames, 118, 122);          // ~120 frames (fps boundary rounding)
            Assert.Equal(frames, counter.Count);       // the compositor saw every frame
            Assert.True(File.Exists(outPath));

            var exported = MediaProbe.Probe(ffmpeg, outPath);
            Assert.NotNull(exported);
            Assert.Equal(4.0, exported!.Duration.TotalSeconds, 1);
            Assert.Equal(source.Width, exported.Width);
            Assert.Equal(source.Height, exported.Height);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Cuts_are_applied_in_the_render()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg) return;

        var dir = Path.Combine(Path.GetTempPath(), $"shrike-composite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var srcPath = Path.Combine(dir, "src.mp4");
        var outPath = Path.Combine(dir, "out.mp4");
        try
        {
            Run(ffmpeg, "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc=duration=6:size=320x240:rate=30",
                "-pix_fmt", "yuv420p", srcPath);
            var source = MediaProbe.Probe(ffmpeg, srcPath);
            Assert.NotNull(source);

            var timeline = new Timeline(source!);
            timeline.Cut(2_000, 4_000);                // keep [0,2) + [4,6) = 4 s
            Assert.Equal(4_000, timeline.KeptDurationMs);

            var pipeline = new FrameCompositePipeline(ffmpeg);
            pipeline.Run(source!, timeline.KeptRanges,
                source!.Width, source.Height, source.Fps, bitrate: 2_000_000, outPath, new IdentityCompositor());

            var exported = MediaProbe.Probe(ffmpeg, outPath);
            Assert.NotNull(exported);
            Assert.Equal(4.0, exported!.Duration.TotalSeconds, 1);   // kept length, not the 6 s source
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Composite_then_export_honours_the_chosen_preset()
    {
        // Parity check: stage 1 composites to a high-quality intermediate, stage 2 encodes it with a real
        // preset (H.265) — the same two-stage path the export dialog uses so every preset gets the cursor.
        if (Ffmpeg.Locate() is not { } ffmpeg) return;

        var dir = Path.Combine(Path.GetTempPath(), $"shrike-parity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var srcPath = Path.Combine(dir, "src.mp4");
        var interPath = Path.Combine(dir, "inter.mp4");
        var outPath = Path.Combine(dir, "out.mp4");
        try
        {
            Run(ffmpeg, "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc=duration=3:size=320x240:rate=30",
                "-pix_fmt", "yuv420p", srcPath);
            var source = MediaProbe.Probe(ffmpeg, srcPath);
            Assert.NotNull(source);

            var timeline = new Timeline(source!);

            // Stage 1 — composite (identity is enough to prove the chain) to the intermediate.
            new FrameCompositePipeline(ffmpeg).Run(source!, timeline.KeptRanges,
                source!.Width, source.Height, source.Fps, bitrate: 20_000_000, interPath, new IdentityCompositor());
            Assert.True(File.Exists(interPath));

            // Stage 2 — encode the intermediate with an H.265 preset via the normal exporter.
            var interSource = new RecordingSource(interPath, source.Width, source.Height, source.Fps, source.Duration);
            var profile = ExportProfile.Presets.First(p => p.Codec == ExportCodec.H265);
            var cmd = ExportCommand.Build(interSource, new Timeline(interSource).KeptRanges, profile, hardware: null, outPath);
            await new VideoExporter(ffmpeg).ExportAsync(cmd, timeline.KeptDurationMs, new Progress<double>());

            var exported = MediaProbe.Probe(ffmpeg, outPath);
            Assert.NotNull(exported);
            Assert.Equal(3.0, exported!.Duration.TotalSeconds, 1); // survives both stages at the right length
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg setup failed: {err}");
    }
}
