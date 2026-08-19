namespace Shrike.Core.Audio;

/// <summary>
/// Streams a live <see cref="IAudioSource"/> into a WAV sidecar during a recording — the audio counterpart to
/// <c>MouseTrackRecorder</c>. Buffers are written only while the recording clock is running: when
/// <c>captureTimeMs</c> returns null (paused / stopped / not started) the buffer is dropped, so the sidecar
/// excludes paused spans exactly as the video does and the two stay aligned without a shared sample clock.
/// The source raises <see cref="IAudioSource.DataAvailable"/> on its capture thread, so writes are locked.
/// </summary>
public sealed class AudioCaptureRecorder : IDisposable
{
    private readonly IAudioSource _source;
    private readonly WavWriter _writer;
    private readonly Func<long?> _captureTimeMs;
    private readonly object _lock = new();
    private bool _started;
    private bool _disposed;
    private long _droppedBuffers;

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

    /// <summary>PCM bytes written to the sidecar so far.</summary>
    public long BytesWritten { get { lock (_lock) return _writer.DataBytes; } }

    /// <summary>Buffers dropped because the recording was paused/stopped when they arrived.</summary>
    public long DroppedBuffers { get { lock (_lock) return _droppedBuffers; } }

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

    private void OnData(byte[] pcm)
    {
        // Drop anything captured while the recording clock isn't running (paused / stopped).
        if (_captureTimeMs() is null)
        {
            lock (_lock) _droppedBuffers++;
            return;
        }
        lock (_lock)
        {
            if (_disposed) return;
            _writer.Write(pcm);
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
