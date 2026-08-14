using System.Diagnostics;

namespace Shrike.Core.Recording;

/// <summary>
/// Drives a recording end to end: a background thread grabs frames from an <see cref="IFrameSource"/>
/// at the target rate and feeds them, with a monotonic clock reading, to a <see cref="RecordingSession"/>
/// (which paces to real time and writes through the encoder). The HUD calls <see cref="Pause"/> /
/// <see cref="Resume"/> / <see cref="Stop"/> / <see cref="Discard"/> from the UI thread; the session is
/// not thread-safe, so every touch is serialised behind one lock.
/// </summary>
public sealed class Recorder : IDisposable
{
    private readonly IFrameSource _source;
    private readonly RecordingSession _session;
    private readonly int _fps;
    private readonly Stopwatch _clock = new();
    private readonly object _gate = new();

    private Thread? _thread;
    private volatile bool _running;

    public string OutputPath { get; }
    public RecordingState State => _session.State;

    /// <summary>Frame size and rate of the source — the facts the timeline editor needs to build a <see cref="RecordingSource"/>.</summary>
    public int Width => _source.Width;
    public int Height => _source.Height;
    public int Fps => _fps;

    /// <summary>Final recorded (un-paused) length, captured at <see cref="Stop"/>. Zero until then.</summary>
    public TimeSpan Duration { get; private set; }

    public Recorder(IFrameSource source, IFrameEncoder encoder, string outputPath, int fps)
    {
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        _source = source;
        _session = new RecordingSession(encoder, fps);
        _fps = fps;
        OutputPath = outputPath;
    }

    /// <summary>Elapsed recorded time (excludes paused spans) — for the HUD clock.</summary>
    public TimeSpan Elapsed => TimeSpan.FromMilliseconds(_session.ElapsedMs(_clock.ElapsedMilliseconds));

    /// <summary>
    /// The current position on the recording timeline in milliseconds (pause-excluded), or null when not
    /// actively recording (paused, stopped, or not started). The smooth-cursor track stamps each sample
    /// with this so the track shares the video's timeline exactly — samples during a pause are dropped.
    /// </summary>
    public long? CaptureTimeMs()
    {
        lock (_gate)
        {
            return _session.State == RecordingState.Recording
                ? _session.ElapsedMs(_clock.ElapsedMilliseconds)
                : null;
        }
    }

    public void Start()
    {
        lock (_gate) _session.Start(0);
        _clock.Restart();
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "shrike-recorder" };
        _thread.Start();
    }

    public void Pause()
    {
        lock (_gate) _session.Pause(_clock.ElapsedMilliseconds);
    }

    public void Resume()
    {
        lock (_gate) _session.Resume(_clock.ElapsedMilliseconds);
    }

    /// <summary>Stop the loop, finalise the file, and return its path.</summary>
    public string Stop()
    {
        StopLoop();
        lock (_gate)
        {
            // Freeze the length before stopping the clock, so the editor gets the real recorded duration.
            Duration = TimeSpan.FromMilliseconds(_session.ElapsedMs(_clock.ElapsedMilliseconds));
            _session.Stop();
        }
        _clock.Stop();
        return OutputPath;
    }

    /// <summary>Stop the loop and throw the recording away (deletes the partial file).</summary>
    public void Discard()
    {
        StopLoop();
        lock (_gate) _session.Discard();
        try { if (File.Exists(OutputPath)) File.Delete(OutputPath); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        StopLoop();
        _session.Dispose();
        _source.Dispose();
    }

    private void StopLoop()
    {
        if (!_running && _thread is null) return;
        _running = false;
        _thread?.Join(2000);
        _thread = null;
    }

    private void Loop()
    {
        var intervalMs = 1000.0 / _fps;
        while (_running)
        {
            var frameStart = _clock.ElapsedMilliseconds;

            if (State == RecordingState.Paused)
            {
                Thread.Sleep((int)intervalMs);
                continue;
            }

            byte[]? frame = null;
            try { frame = _source.CaptureFrame(); }
            catch { /* transient grab failure — skip this frame, keep the timeline going */ }

            if (frame is not null)
            {
                lock (_gate)
                {
                    if (_session.State == RecordingState.Recording)
                        _session.Tick(_clock.ElapsedMilliseconds, frame);
                }
            }

            // Pace to the target interval; capture time is absorbed into the wait.
            var spent = _clock.ElapsedMilliseconds - frameStart;
            var wait = intervalMs - spent;
            if (wait >= 1) Thread.Sleep((int)wait);
        }
    }
}
