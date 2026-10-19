using System.Diagnostics;

namespace Shrike.Core.Audio;

/// <summary>
/// Streams a live <see cref="IAudioSource"/> into a WAV sidecar during a recording — the audio counterpart to
/// <c>MouseTrackRecorder</c>. Buffers are written only while the recording clock is running: when
/// <c>captureTimeMs</c> returns null (paused / stopped / not started) the buffer is dropped, so the sidecar
/// excludes paused spans exactly as the video does.
/// <para>
/// Each buffer is placed by its capture timestamp, not just appended: a source can go quiet (WASAPI loopback
/// delivers <i>nothing</i> while no app is playing sound), start late (the device opens after the recorder
/// starts), or lose samples to an overrun. Any such hole is filled with silence so the sidecar stays on the
/// video's timeline — otherwise every quiet stretch would pull the rest of the audio earlier.
/// </para>
/// The source raises <see cref="IAudioSource.DataAvailable"/> on its capture thread, so writes are locked.
/// </summary>
public sealed class AudioCaptureRecorder : IDisposable
{
    // Holes shorter than this are timing jitter, not missing audio — left alone so continuous capture is
    // never chopped up with micro-gaps.
    private const long GapToleranceMs = 20;

    private static readonly byte[] Silence = new byte[64 * 1024];

    private readonly IAudioSource _source;
    private readonly WavWriter _writer;
    private readonly Func<long?> _captureTimeMs;
    private readonly object _lock = new();
    private bool _started;
    private bool _disposed;
    private long _droppedBuffers;
    private long _silencePaddedMs;

    /// <param name="source">The live audio source; its <see cref="IAudioSource.Format"/> must match the writer.</param>
    /// <param name="writer">Open sidecar writer this recorder owns and finalises on <see cref="Dispose"/>.</param>
    /// <param name="captureTimeMs">The recording's pause-excluded clock — see <c>Recorder.CaptureTimeMs</c>.</param>
    public AudioCaptureRecorder(IAudioSource source, WavWriter writer, Func<long?> captureTimeMs)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _captureTimeMs = captureTimeMs ?? throw new ArgumentNullException(nameof(captureTimeMs));
        if (source.Format != writer.Format)
            throw new ArgumentException($"Source format {source.Format} does not match sidecar {writer.Format}.");
    }

    /// <summary>PCM bytes written to the sidecar so far (captured audio plus any silence padding).</summary>
    public long BytesWritten { get { lock (_lock) return _writer.DataBytes; } }

    /// <summary>Buffers dropped because the recording was paused/stopped when they arrived.</summary>
    public long DroppedBuffers { get { lock (_lock) return _droppedBuffers; } }

    /// <summary>Total silence inserted to cover holes in the source's delivery.</summary>
    public long SilencePaddedMs { get { lock (_lock) return _silencePaddedMs; } }

    /// <summary>Subscribe and start the source. Idempotent.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed) return;
            _started = true;
        }
        _source.DataAvailable += OnData;
        _source.Start();
    }

    /// <summary>Stop the source and unsubscribe. The sidecar is finalised on <see cref="Dispose"/>.</summary>
    public void Stop()
    {
        _source.DataAvailable -= OnData;
        _source.Stop();
    }

    private void OnData(byte[] pcm, long captureTimestamp)
    {
        // Drop anything captured while the recording clock isn't running (paused / stopped).
        if (_captureTimeMs() is not { } nowMs)
        {
            lock (_lock) _droppedBuffers++;
            return;
        }

        // Where this buffer's first sample belongs on the recording timeline: the clock now, less how long ago
        // the sample was captured. Read right after the clock so a slow callback doesn't skew it.
        var startMs = nowMs - (long)Stopwatch.GetElapsedTime(captureTimestamp).TotalMilliseconds;

        lock (_lock)
        {
            if (_disposed) return;
            var gapMs = startMs - _writer.Format.BytesToMs(_writer.DataBytes);
            if (gapMs > GapToleranceMs)
            {
                WriteSilence(_writer.Format.MsToBytes(gapMs));
                _silencePaddedMs += gapMs;
            }
            _writer.Write(pcm);
        }
    }

    private void WriteSilence(long bytes)
    {
        while (bytes > 0)
        {
            var n = (int)Math.Min(bytes, Silence.Length);
            _writer.Write(Silence.AsSpan(0, n));
            bytes -= n;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        _source.Dispose();
        lock (_lock) _writer.Dispose(); // patches WAV sizes and closes the sidecar
    }
}
