using System.Buffers.Binary;
using System.Diagnostics;
using Shrike.Core.Recording;

namespace Shrike.Tests;

/// <summary>
/// The timeline-preview primitive: pull a still frame out of a clip as PNG. Synthesises a source with
/// ffmpeg's test pattern so it needs no screen; no-ops when ffmpeg is absent.
/// </summary>
public class FrameExtractorTests
{
    [Fact]
    public void Extracts_a_downscaled_png_frame_at_a_time()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg) return;

        var dir = Path.Combine(Path.GetTempPath(), $"shrike-frame-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "src.mp4");
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                "-i", "testsrc=duration=4:size=320x240:rate=30", "-pix_fmt", "yuv420p", src })
                psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi)!) { _ = p.StandardError.ReadToEnd(); p.WaitForExit(30_000); }

            var extractor = new FrameExtractor(ffmpeg, src);
            var png = extractor.ExtractPng(2_000, maxHeight: 120);

            Assert.NotNull(png);
            // PNG signature.
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png!.AsSpan(0, 4).ToArray());
            // IHDR width/height (big-endian at bytes 16..24). Scaled to height 120, width 160 (4:3).
            var w = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
            var h = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
            Assert.Equal(120, h);
            Assert.Equal(160, w);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
