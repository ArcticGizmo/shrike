using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Shrike.Core.Recording;

/// <summary>
/// Reads the facts M5 needs about a video file — duration, pixel size, frame rate — by parsing
/// <c>ffmpeg -i</c>'s stderr banner (the lean bundle has no <c>ffprobe</c>). The recorder already hands
/// the editor a <see cref="RecordingSource"/> with these known, so this is for re-opening a clip from
/// disk and for verifying export output. Returns null when it can't parse, so callers degrade gracefully.
/// </summary>
public static partial class MediaProbe
{
    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationRe();

    [GeneratedRegex(@"Video:.*?,\s*(\d{2,5})x(\d{2,5})")]
    private static partial Regex SizeRe();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*fps")]
    private static partial Regex FpsRe();

    /// <summary>Probe a file into a <see cref="RecordingSource"/>, or null if it can't be read/parsed.</summary>
    public static RecordingSource? Probe(string ffmpegPath, string filePath)
    {
        var banner = RunBanner(ffmpegPath, filePath);
        if (banner is null) return null;

        var duration = ParseDuration(banner);
        var size = SizeRe().Match(banner);
        var fps = FpsRe().Match(banner);
        if (duration is null || !size.Success) return null;

        var w = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
        var h = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
        var f = fps.Success
            ? (int)Math.Round(double.Parse(fps.Groups[1].Value, CultureInfo.InvariantCulture))
            : 30;
        return new RecordingSource(filePath, w, h, Math.Max(1, f), duration.Value);
    }

    /// <summary>Just the duration, or null. Public + string-based so it's headless-testable.</summary>
    public static TimeSpan? ParseDuration(string banner)
    {
        var m = DurationRe().Match(banner);
        if (!m.Success) return null;
        var hours = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var mins = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var secs = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return new TimeSpan(0, hours, mins, 0) + TimeSpan.FromSeconds(secs);
    }

    private static string? RunBanner(string ffmpegPath, string filePath)
    {
        try
        {
            // `ffmpeg -i file` with no output prints stream info to stderr and exits non-zero — that's fine.
            using var p = Process.Start(new ProcessStartInfo(ffmpegPath)
            {
                ArgumentList = { "-hide_banner", "-i", filePath },
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return stderr;
        }
        catch
        {
            return null;
        }
    }
}
