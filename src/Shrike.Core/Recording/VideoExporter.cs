using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Shrike.Core.Recording;

/// <summary>
/// Runs an <see cref="ExportCommand"/> off the UI thread and reports progress. It asks ffmpeg for
/// machine-readable progress (<c>-progress pipe:1</c>) and turns the streamed <c>out_time_us</c> against
/// the known kept duration into a 0..1 fraction, so the export dialog can show a real bar and never
/// blocks. Cancellation kills ffmpeg and deletes the partial file.
/// </summary>
public sealed class VideoExporter
{
    private readonly string _ffmpegPath;

    public VideoExporter(string ffmpegPath) => _ffmpegPath = ffmpegPath;

    /// <summary>
    /// Encode <paramref name="command"/> to its output path. <paramref name="totalDurationMs"/> is the
    /// edited (kept) length, used to scale progress. Throws with ffmpeg's diagnostics on failure.
    /// </summary>
    public async Task ExportAsync(ExportCommand command, long totalDurationMs,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // -progress writes key=value lines to stdout; -nostats keeps stderr to real errors.
        psi.ArgumentList.Add("-progress");
        psi.ArgumentList.Add("pipe:1");
        psi.ArgumentList.Add("-nostats");
        foreach (var a in command.Arguments) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");

        var stderrTail = new StringBuilder();
        var stderrTask = DrainStderrAsync(proc, stderrTail);
        var progressTask = PumpProgressAsync(proc, totalDurationMs, progress);

        try
        {
            await proc.WaitForExitAsync(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            await Safe(progressTask); await Safe(stderrTask);
            TryDelete(command);
            throw;
        }

        await Safe(progressTask);
        await Safe(stderrTask);

        if (proc.ExitCode != 0)
        {
            TryDelete(command);
            var tail = stderrTail.ToString().Trim();
            throw new InvalidOperationException(
                $"ffmpeg export failed (exit {proc.ExitCode}).{(tail.Length == 0 ? "" : " " + tail)}");
        }

        progress?.Report(1.0);
    }

    private static async Task PumpProgressAsync(Process proc, long totalMs, IProgress<double>? progress)
    {
        if (progress is null || totalMs <= 0) { proc.StandardOutput.BaseStream.Dispose(); return; }

        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            // Lines like "out_time_us=1500000" or "out_time_ms=1500000" (ffmpeg's *_ms is actually µs).
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var us))
            {
                var frac = Math.Clamp(us / 1000.0 / totalMs, 0, 1);
                progress.Report(frac);
            }
        }
    }

    private static async Task DrainStderrAsync(Process proc, StringBuilder tail)
    {
        string? line;
        while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            tail.AppendLine(line);
            if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000);
        }
    }

    private static async Task Safe(Task t) { try { await t.ConfigureAwait(false); } catch { /* ignore */ } }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ignore */ }
    }

    private static void TryDelete(ExportCommand command)
    {
        var path = command.Arguments[^1];
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
