using System.Diagnostics;
using System.Globalization;

namespace Shrike.Core.Recording;

/// <summary>
/// Pulls a single still frame out of a recording as PNG bytes, using the bundled ffmpeg. This is the one
/// primitive behind the timeline editor's preview: the scrubber, the filmstrip thumbnails, and the
/// timer-driven play button all just ask for "the frame at source time T" (optionally downscaled). Uses
/// fast keyframe seek (<c>-ss</c> before <c>-i</c>), so a scrub lands on the nearest keyframe — plenty
/// accurate to find cut points, and cheap enough to feel responsive.
/// </summary>
public sealed class FrameExtractor
{
    private readonly string _ffmpegPath;
    private readonly string _sourcePath;

    public FrameExtractor(string ffmpegPath, string sourcePath)
    {
        _ffmpegPath = ffmpegPath;
        _sourcePath = sourcePath;
    }

    /// <summary>
    /// The frame at <paramref name="sourceMs"/> as PNG bytes, or null on failure. When
    /// <paramref name="maxHeight"/> is set the frame is scaled down to that height (width auto, even).
    /// </summary>
    public byte[]? ExtractPng(long sourceMs, int? maxHeight = null, int timeoutMs = 8000)
    {
        var sec = (Math.Max(0, sourceMs) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(sec);
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(_sourcePath);
        if (maxHeight is { } h)
        {
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add($"scale=-2:{h & ~1}:flags=lanczos");
        }
        psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("image2pipe");
        psi.ArgumentList.Add("-vcodec"); psi.ArgumentList.Add("png");
        psi.ArgumentList.Add("pipe:1");

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;

            // Drain stderr off-thread so a chatty ffmpeg can't deadlock the stdout pipe.
            var errTask = Task.Run(() => { try { _ = p.StandardError.ReadToEnd(); } catch { } });

            using var buffer = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(buffer);

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            errTask.Wait(500);

            return p.ExitCode == 0 && buffer.Length > 0 ? buffer.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }
}
