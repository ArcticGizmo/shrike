namespace Shrike.Core.Recording;

public enum RecordingState { Idle, Recording, Paused, Stopped }

/// <summary>
/// Paces a stream of captured frames to a constant output frame rate and drives an
/// <see cref="IFrameEncoder"/>. The caller pushes the current frame via <see cref="Tick"/> at roughly
/// capture cadence, passing a monotonic clock reading; the session decides how many output frames to
/// emit so the finished video runs at real-time speed regardless of jitter:
/// <list type="bullet">
///   <item>capture slower than target fps → the last frame is duplicated to fill the gap;</item>
///   <item>capture faster than target fps → extra ticks emit nothing.</item>
/// </list>
/// Pause excludes wall-time from the timeline. All timing is caller-supplied (milliseconds), so the
/// logic is deterministic and headless-testable; the real driver (WGC capture loop) supplies a
/// stopwatch reading. Not thread-safe — drive from the capture thread.
/// </summary>
public sealed class RecordingSession : IDisposable
{
    private readonly IFrameEncoder _encoder;
    private readonly int _fps;

    private long _startMs = -1;
    private long _pausedAccumMs;   // total paused wall-time excluded from the timeline
    private long _pauseStartMs = -1;
    private long _framesWritten;

    public RecordingState State { get; private set; } = RecordingState.Idle;
    public int Fps => _fps;
    public long FramesWritten => _framesWritten;

    public RecordingSession(IFrameEncoder encoder, int fps)
    {
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        _encoder = encoder;
        _fps = fps;
    }

    /// <summary>Begin the timeline at <paramref name="nowMs"/>. Only valid from <see cref="RecordingState.Idle"/>.</summary>
    public void Start(long nowMs)
    {
        Expect(RecordingState.Idle, nameof(Start));
        _startMs = nowMs;
        State = RecordingState.Recording;
    }

    /// <summary>Active (un-paused) time on the timeline so far, in milliseconds.</summary>
    public long ElapsedMs(long nowMs)
    {
        if (_startMs < 0) return 0;
        var elapsed = nowMs - _startMs - _pausedAccumMs;
        if (State == RecordingState.Paused && _pauseStartMs >= 0)
            elapsed -= nowMs - _pauseStartMs;   // don't count the in-progress pause
        return Math.Max(0, elapsed);
    }

    /// <summary>
    /// Offer the current frame at clock reading <paramref name="nowMs"/>. Emits as many output frames as
    /// the elapsed timeline calls for (0..n). No-op unless recording. Returns the number of frames written.
    /// </summary>
    public int Tick(long nowMs, byte[] frameBgra)
    {
        if (State != RecordingState.Recording) return 0;

        var elapsed = ElapsedMs(nowMs);
        var written = 0;
        // Frame index i belongs at timestamp i*1000/fps ms; emit every frame whose time has arrived.
        while (_framesWritten * 1000L / _fps <= elapsed)
        {
            _encoder.WriteFrame(frameBgra);
            _framesWritten++;
            written++;
        }
        return written;
    }

    public void Pause(long nowMs)
    {
        Expect(RecordingState.Recording, nameof(Pause));
        _pauseStartMs = nowMs;
        State = RecordingState.Paused;
    }

    public void Resume(long nowMs)
    {
        Expect(RecordingState.Paused, nameof(Resume));
        if (_pauseStartMs >= 0)
            _pausedAccumMs += nowMs - _pauseStartMs;
        _pauseStartMs = -1;
        State = RecordingState.Recording;
    }

    /// <summary>Finalise the output file. Idempotent; valid from Recording/Paused (or a no-op if never started).</summary>
    public void Stop()
    {
        if (State is RecordingState.Stopped or RecordingState.Idle)
        {
            State = RecordingState.Stopped;
            return;
        }
        State = RecordingState.Stopped;
        _encoder.Finish();
    }

    /// <summary>Abandon the recording without finalising — the partial file is the caller's to delete.</summary>
    public void Discard()
    {
        State = RecordingState.Stopped;
        _encoder.Dispose();
    }

    public void Dispose() => _encoder.Dispose();

    private void Expect(RecordingState required, string op)
    {
        if (State != required)
            throw new InvalidOperationException($"{op} is not valid from state {State} (requires {required}).");
    }
}
