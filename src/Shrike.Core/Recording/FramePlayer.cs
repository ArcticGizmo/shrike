using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Shrike.Core.Recording;

/// <summary>
/// Real-time preview playback from a single, persistent ffmpeg process. Rather than spawning ffmpeg per
/// frame (which can't sustain any frame rate), this decodes the kept ranges once — trim+concat, scaled to
/// a fixed preview size, at a fixed fps — and streams raw top-down BGRA frames down a pipe. A background
/// thread reads whole frames into a small bounded queue (so the pipe back-pressures ffmpeg to roughly
/// real time), and the UI pulls one frame per timer tick via <see cref="TryTakeFrame"/>. Every frame is
/// exactly <see cref="Width"/>*<see cref="Height"/>*4 bytes.
/// </summary>
public sealed class FramePlayer : IDisposable
{
    private readonly string _ffmpegPath;
    private readonly RecordingSource _source;

    private Process? _proc;
    private Thread? _reader;
    private BlockingCollection<byte[]>? _queue;
    private CancellationTokenSource? _cts;
    private volatile bool _ended;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Fps { get; private set; }

    /// <summary>True once ffmpeg has finished and every buffered frame has been consumed.</summary>
    public bool Ended => _ended && (_queue?.Count ?? 0) == 0;

    public FramePlayer(string ffmpegPath, RecordingSource source)
    {
        _ffmpegPath = ffmpegPath;
        _source = source;
    }

    /// <summary>
    /// Start decoding <paramref name="keptRanges"/> (source-time spans, e.g. from
    /// <see cref="Timeline.KeptRangesFrom"/>) at <paramref name="targetHeight"/> and <paramref name="fps"/>.
    /// Frames then flow; poll <see cref="TryTakeFrame"/>.
    /// </summary>
    public void Start(IReadOnlyList<Segment> keptRanges, int targetHeight, int fps)
    {
        if (keptRanges.Count == 0) throw new ArgumentException("Nothing to play.", nameof(keptRanges));

        Height = Math.Max(2, targetHeight & ~1);
        Width = Math.Max(2, Even((int)Math.Round(_source.Width * (double)Height / _source.Height)));
        Fps = Math.Max(1, fps);

        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(_source.Path);
        psi.ArgumentList.Add("-filter_complex"); psi.ArgumentList.Add(BuildFilter(keptRanges));
        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[v]");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("pipe:1");

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        _cts = new CancellationTokenSource();
        _queue = new BlockingCollection<byte[]>(boundedCapacity: Math.Max(4, Fps));   // ~1s of buffer
        _ended = false;

        // Drain stderr so a chatty ffmpeg can't wedge the stdout pipe.
        var proc = _proc;
        new Thread(() => { try { _ = proc.StandardError.ReadToEnd(); } catch { } })
            { IsBackground = true, Name = "frameplayer-stderr" }.Start();

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "frameplayer-reader" };
        _reader.Start();
    }

    /// <summary>Next decoded frame (top-down BGRA), or null if none is buffered yet.</summary>
    public byte[]? TryTakeFrame()
    {
        var q = _queue;
        if (q is null) return null;
        return q.TryTake(out var frame) ? frame : null;
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
        _reader?.Join(500);
        _reader = null;
        _queue?.Dispose();
        _queue = null;
        try { _proc?.Dispose(); } catch { }
        _proc = null;
    }

    public void Dispose() => Stop();

    private void ReadLoop()
    {
        var frameSize = Width * Height * 4;
        var stream = _proc!.StandardOutput.BaseStream;
        var token = _cts!.Token;
        try
        {
            var buf = new byte[frameSize];
            while (!token.IsCancellationRequested)
            {
                var read = 0;
                while (read < frameSize)
                {
                    var n = stream.Read(buf, read, frameSize - read);
                    if (n <= 0) return;   // EOF — end of stream
                    read += n;
                }
                var frame = new byte[frameSize];
                Array.Copy(buf, frame, frameSize);
                _queue!.Add(frame, token);   // blocks when full → back-pressures ffmpeg to real time
            }
        }
        catch { /* cancelled or pipe closed */ }
        finally { _ended = true; }
    }

    private string BuildFilter(IReadOnlyList<Segment> ranges)
    {
        var chains = new List<string>();
        for (var i = 0; i < ranges.Count; i++)
            chains.Add($"[0:v]trim=start={Sec(ranges[i].StartMs)}:end={Sec(ranges[i].EndMs)}," +
                       $"setpts=PTS-STARTPTS[t{i}]");

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

        // Fixed output size + fps so every frame is exactly Width*Height*4 and paces to the timer.
        chains.Add($"{body}scale={Width}:{Height}:flags=bilinear,fps={Fps},format=bgra[v]");
        return string.Join(";", chains);
    }

    private static string Sec(long ms) => (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
    private static int Even(int n) => n & ~1;
}
