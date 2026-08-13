using System.Diagnostics;
using Shrike.Core.Recording;

namespace Shrike.Tests;

/// <summary>
/// The streaming preview player: one persistent ffmpeg should emit a run of correctly-sized raw frames
/// for the edited timeline. Synthesises a source with ffmpeg; no-ops when ffmpeg is absent.
/// </summary>
public class PlaybackTests
{
    [Fact]
    public void Streams_raw_frames_for_the_kept_ranges()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg) return;

        var dir = Path.Combine(Path.GetTempPath(), $"shrike-play-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "src.mp4");
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                "-i", "testsrc=duration=3:size=320x240:rate=30", "-pix_fmt", "yuv420p", src })
                psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi)!) { _ = p.StandardError.ReadToEnd(); p.WaitForExit(30_000); }

            var source = new RecordingSource(src, 320, 240, 30, TimeSpan.FromSeconds(3));
            var timeline = new Timeline(source);
            timeline.Cut(1_000, 2_000);   // keep 2 seconds

            using var player = new FramePlayer(ffmpeg, source);
            player.Start(timeline.KeptRangesFrom(0), targetHeight: 120, fps: 15);

            Assert.Equal(120, player.Height);
            Assert.Equal(160, player.Width);   // 320 * 120/240
            var frameSize = player.Width * player.Height * 4;

            var frames = 0;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                var f = player.TryTakeFrame();
                if (f is not null) { Assert.Equal(frameSize, f.Length); frames++; }
                else if (player.Ended) break;
                else Thread.Sleep(10);
            }

            // ~2s of kept footage at 15fps ≈ 30 frames; assert we got a real run, not one-off.
            Assert.True(frames >= 10, $"only {frames} frames streamed");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
