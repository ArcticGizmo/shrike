namespace Shrike.Core.Audio;

/// <summary>
/// A source of live PCM audio — the seam between "how we grab samples" (a WASAPI mic or system-loopback
/// tap) and "what we do with them" (write a sidecar, meter the level). Unlike <c>IFrameSource</c>, audio is
/// <b>push-based</b>: the device raises <see cref="DataAvailable"/> on its own capture thread whenever a
/// buffer is ready, because samples arrive on the hardware's clock, not ours. The NAudio implementation
/// ships in the app-side adapter; Core stays dependency-free. Handlers get a freshly-allocated buffer they
/// own; keep the callback light — it runs on the capture thread.
/// </summary>
public interface IAudioSource : IDisposable
{
    /// <summary>Format of every buffer delivered by <see cref="DataAvailable"/> (normalised, see
    /// <see cref="AudioFormat.Default"/>).</summary>
    AudioFormat Format { get; }

    /// <summary>Raised on the capture thread with a fresh PCM buffer in <see cref="Format"/>. The buffer is
    /// exactly the captured length and belongs to the handler.</summary>
    event Action<byte[]>? DataAvailable;

    /// <summary>Begin delivering buffers. Idempotent while running.</summary>
    void Start();

    /// <summary>Stop delivering buffers. Safe to call when already stopped.</summary>
    void Stop();
}
