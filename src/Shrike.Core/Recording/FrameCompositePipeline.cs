using System.Diagnostics;
using System.Globalization;

namespace Shrike.Core.Recording;

/// <summary>
/// The SC3 "Option B" render pass: decode a recording's edited (trimmed) frames to raw BGRA with ffmpeg,
/// hand each frame to an <see cref="IFrameCompositor"/> to draw on, and pipe the result into a second
/// ffmpeg to re-encode — a single decode → compose → encode so no generation loss stacks. The decode stage
/// applies the kept ranges (trim + concat) and scales to the output size, so the compositor and encoder
/// only ever see final-resolution edited frames. With an <see cref="IdentityCompositor"/> this reproduces
/// the edited video unchanged; SC4 swaps in the cursor compositor. Progress is reported per frame and the
/// pass is cancellable (both ffmpegs are torn down and the partial output deleted).
/// </summary>
public sealed class FrameCompositePipeline
{
    private readonly string _ffmpegPath;

    public FrameCompositePipeline(string ffmpegPath) => _ffmpegPath = ffmpegPath;

    /// <summary>
    /// Render <paramref name="keptRanges"/> of <paramref name="source"/> through <paramref name="compositor"/>
    /// to an H.264 MP4 at <paramref name="outputPath"/>, sized <paramref name="outWidth"/>×<paramref name="outHeight"/>
    /// at <paramref name="fps"/>. Returns the number of frames written. Throws (and cleans up) on failure or cancel.
    /// </summary>
    public long Run(
        RecordingSource source, IReadOnlyList<Segment> keptRanges,
        int outWidth, int outHeight, int fps, int bitrate, string outputPath,
        IFrameCompositor compositor, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        if (keptRanges.Count == 0) throw new ArgumentException("Nothing to render.", nameof(keptRanges));
        outWidth = Even(outWidth);
        outHeight = Even(outHeight);
        if (outWidth < 2 || outHeight < 2) throw new ArgumentException("Output size is too small.");
        if (fps <= 0) throw new ArgumentException("Frame rate must be positive.", nameof(fps));

        var frameBytes = outWidth * outHeight * 4;
        var keptMs = keptRanges.Sum(r => r.DurationMs);
        var expectedFrames = Math.Max(1L, (long)(keptMs / 1000.0 * fps));

        var decode = StartDecode(source.Path, keptRanges, outWidth, outHeight, fps);
        // Drain decode's stderr so a chatty ffmpeg can't wedge the stdout pipe we're reading.
        var stderrPump = new Thread(() => { try { _ = decode.StandardError.ReadToEnd(); } catch { /* closed on exit */ } })
        { IsBackground = true, Name = "composite-decode-stderr" };
        stderrPump.Start();

        var encoder = new FfmpegMp4Encoder(_ffmpegPath, outputPath, outWidth, outHeight, fps, bitrate);
        var stdout = decode.StandardOutput.BaseStream;
        var buf = new byte[frameBytes];
        long frameIndex = 0;

        try
        {
            while (true)
            {
                cancel.ThrowIfCancellationRequested();

                // Read exactly one frame (the pipe may hand it over in several chunks).
                var read = 0;
                while (read < frameBytes)
                {
                    var n = stdout.Read(buf, read, frameBytes - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < frameBytes) break; // clean EOF between frames (read == 0) or a trailing partial

                compositor.Compose(buf, outWidth, outHeight, (int)frameIndex);
                encoder.WriteFrame(buf);
                frameIndex++;

                if ((frameIndex & 7) == 0)
                    progress?.Report(Math.Clamp(frameIndex / (double)expectedFrames, 0, 1));
            }

            encoder.Finish();
            progress?.Report(1.0);
            return frameIndex;
        }
        catch
        {
            encoder.Dispose();      // kill the encoder; the partial file is ours to remove
            KillDecode(decode);
            TryDelete(outputPath);
            throw;
        }
        finally
        {
            KillDecode(decode);
            stderrPump.Join(500);
        }
    }

    private Process StartDecode(string sourcePath, IReadOnlyList<Segment> ranges, int w, int h, int fps)
    {
        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(sourcePath);
        psi.ArgumentList.Add("-filter_complex"); psi.ArgumentList.Add(BuildFilter(ranges, w, h, fps));
        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[v]");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("pipe:1");
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg (decode).");
    }

    // trim+concat the kept ranges, then fix the output size + fps + pixel format so every frame is exactly
    // w*h*4 BGRA bytes — the same shape FramePlayer uses for preview, but Lanczos-scaled for export quality.
    private static string BuildFilter(IReadOnlyList<Segment> ranges, int w, int h, int fps)
    {
        var chains = new List<string>();
        for (var i = 0; i < ranges.Count; i++)
            chains.Add($"[0:v]trim=start={Sec(ranges[i].StartMs)}:end={Sec(ranges[i].EndMs)},setpts=PTS-STARTPTS[t{i}]");

        string body;
        if (ranges.Count == 1)
        {
            body = "[t0]";
        }
        else
        {
            var inputs = string.Concat(Enumerable.Range(0, ranges.Count).Select(i => $"[t{i}]"));
            chains.Add($"{inputs}concat=n={ranges.Count}:v=1:a=0[c]");
            body = "[c]";
        }

        chains.Add($"{body}scale={w}:{h}:flags=lanczos,fps={fps},format=bgra[v]");
        return string.Join(";", chains);
    }

    private static void KillDecode(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
        try { p.Dispose(); } catch { /* ignore */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static string Sec(long ms) => (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
    private static int Even(int n) => n & ~1;
}
