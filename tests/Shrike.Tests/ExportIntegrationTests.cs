using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Shrike.Core.Recording;

namespace Shrike.Tests;

/// <summary>
/// The M5 exit criterion end-to-end: take a source clip, cut a middle section, export a preset, and
/// confirm the result is a playable MP4 whose duration matches the kept segments. Synthesises the source
/// with ffmpeg's test pattern so it needs no screen — just ffmpeg. No-ops when ffmpeg is absent.
/// </summary>
public class ExportIntegrationTests
{
    [Fact]
    public async Task Trims_and_exports_to_a_playable_mp4_matching_the_kept_ranges()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg) return;

        var dir = Path.Combine(Path.GetTempPath(), $"shrike-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var srcPath = Path.Combine(dir, "src.mp4");
        var outPath = Path.Combine(dir, "out.mp4");
        try
        {
            // A known 6-second, 320x240, 30fps source.
            Run(ffmpeg, "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc=duration=6:size=320x240:rate=30",
                "-pix_fmt", "yuv420p", srcPath);
            Assert.True(File.Exists(srcPath), "source not created");

            var source = MediaProbe.Probe(ffmpeg, srcPath);
            Assert.NotNull(source);
            Assert.Equal(6.0, source!.Duration.TotalSeconds, 1);   // probe round-trips duration
            Assert.Equal(320, source.Width);
            Assert.Equal(240, source.Height);

            // Cut the middle two seconds → keep [0,2) + [4,6) = 4 seconds.
            var timeline = new Timeline(source);
            timeline.Cut(2_000, 4_000);
            Assert.Equal(4_000, timeline.KeptDurationMs);

            var profile = ExportProfile.Presets.First(p => p.Name == "Most compatible");
            var cmd = ExportCommand.Build(source, timeline.KeptRanges, profile, hardware: null, outPath);

            var seen = new List<double>();
            var exporter = new VideoExporter(ffmpeg);
            await exporter.ExportAsync(cmd, timeline.KeptDurationMs, new Progress<double>(seen.Add));

            Assert.True(File.Exists(outPath), "no export produced");
            var data = File.ReadAllBytes(outPath);
            Assert.True(data.Length > 2_000, $"file suspiciously small: {data.Length} bytes");
            Assert.Equal("ftyp", Encoding.ASCII.GetString(data, 4, 4));
            Assert.True(FindBox(data, "moov"), "no moov box — container not finalised");

            var exported = MediaProbe.Probe(ffmpeg, outPath);
            Assert.NotNull(exported);
            Assert.Equal(4.0, exported!.Duration.TotalSeconds, 1);   // matches the kept length, not the source
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

    private static bool FindBox(byte[] data, string fourcc)
    {
        var pos = 0;
        while (pos + 8 <= data.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
            if (Encoding.ASCII.GetString(data, pos + 4, 4) == fourcc) return true;
            if (size < 8) break;
            pos += (int)size;
        }
        return false;
    }
}
