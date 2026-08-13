using System.Diagnostics;
using System.Text;

namespace Shrike.Core.Recording;

/// <summary>
/// Encodes top-down BGRA frames to an H.264 MP4 by piping raw video into ffmpeg's stdin. ffmpeg does the
/// BGRA→YUV conversion and libx264 encoding, so Shrike carries no in-process codec. Each
/// <see cref="WriteFrame"/> is one output frame at the fixed frame rate (the caller paces to match
/// wall-clock). stderr is drained on a background thread so a chatty ffmpeg can never deadlock the pipe.
/// </summary>
public sealed class FfmpegMp4Encoder : IFrameEncoder
{
    private readonly Process _proc;
    private readonly Stream _stdin;
    private readonly int _frameBytes;
    private readonly StringBuilder _stderrTail = new();
    private readonly Thread _stderrPump;
    private bool _finished;

    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// Start ffmpeg writing an H.264 MP4 to <paramref name="path"/>. <paramref name="ffmpegPath"/> comes
    /// from <see cref="Ffmpeg.Locate"/>. <paramref name="bitrate"/> is the target average bits per second.
    /// </summary>
    public FfmpegMp4Encoder(string ffmpegPath, string path, int width, int height, int fps, int bitrate)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("Frame size must be positive.");
        if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("Recording size must be even (yuv420p).");
        if (fps <= 0) throw new ArgumentException("Frame rate must be positive.", nameof(fps));

        Width = width;
        Height = height;
        _frameBytes = width * height * 4;

        var args = string.Join(' ',
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "rawvideo", "-pixel_format", "bgra",
            "-video_size", $"{width}x{height}", "-framerate", fps.ToString(),
            "-i", "pipe:0",
            "-an",
            "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
            "-b:v", bitrate.ToString(),
            "-movflags", "+faststart",
            Quote(path));

        var psi = new ProcessStartInfo(ffmpegPath, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        _stdin = _proc.StandardInput.BaseStream;

        _stderrPump = new Thread(DrainStderr) { IsBackground = true, Name = "ffmpeg-stderr" };
        _stderrPump.Start();
    }

    public void WriteFrame(byte[] bgra)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (bgra.Length != _frameBytes)
            throw new ArgumentException($"Frame is {bgra.Length} bytes; expected {_frameBytes}.", nameof(bgra));

        try
        {
            _stdin.Write(bgra, 0, bgra.Length);
        }
        catch (IOException ex)
        {
            // Broken pipe => ffmpeg died; surface its diagnostics rather than a bare IO error.
            throw new InvalidOperationException($"ffmpeg stopped accepting frames.{StderrSuffix()}", ex);
        }
    }

    public void Finish()
    {
        if (_finished) return;
        _finished = true;

        try { _stdin.Flush(); _stdin.Close(); } catch { /* ffmpeg may have already exited */ }

        if (!_proc.WaitForExit(30_000))
        {
            TryKill();
            throw new InvalidOperationException("ffmpeg did not finish within 30s; output may be incomplete.");
        }

        _stderrPump.Join(1000);

        if (_proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with code {_proc.ExitCode}.{StderrSuffix()}");
    }

    public void Dispose()
    {
        if (!_finished)
        {
            // Abandoned without finishing (e.g. discard): tear ffmpeg down; the partial file is the caller's to delete.
            try { _stdin.Close(); } catch { /* ignore */ }
            TryKill();
        }
        _proc.Dispose();
    }

    private void DrainStderr()
    {
        try
        {
            string? line;
            while ((line = _proc.StandardError.ReadLine()) is not null)
            {
                lock (_stderrTail)
                {
                    _stderrTail.AppendLine(line);
                    // Keep only the tail so a long run can't grow this unbounded.
                    if (_stderrTail.Length > 4000)
                        _stderrTail.Remove(0, _stderrTail.Length - 4000);
                }
            }
        }
        catch { /* pipe closed on exit */ }
    }

    private string StderrSuffix()
    {
        lock (_stderrTail)
        {
            var text = _stderrTail.ToString().Trim();
            return text.Length == 0 ? "" : $" ffmpeg said: {text}";
        }
    }

    private void TryKill()
    {
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;
}
