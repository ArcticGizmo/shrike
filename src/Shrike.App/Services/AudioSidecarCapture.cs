using System.Runtime.Versioning;
using Shrike.Audio;
using Shrike.Core;
using Shrike.Core.Audio;

namespace Shrike.App.Services;

/// <summary>
/// Captures the microphone and/or system sound into WAV sidecars next to a recording, each aligned to the
/// recorder's pause-excluded clock so the audio shares the video timeline (paused spans are dropped, exactly
/// like the mouse track). Every source is independent and tolerant of a device that won't open — a missing
/// mic just means no mic sidecar, the recording proceeds. <see cref="Dispose"/> finalises the WAVs.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AudioSidecarCapture : IDisposable
{
    private readonly List<AudioCaptureRecorder> _recorders = [];
    private readonly List<string> _paths = [];

    private AudioSidecarCapture() { }

    /// <summary>Sidecar paths that were successfully opened for writing.</summary>
    public IReadOnlyList<string> WrittenPaths => _paths;

    private bool IsCapturing => _recorders.Count > 0;

    /// <summary>Start whichever sources are enabled, writing sidecars derived from
    /// <paramref name="recordingPath"/>. Returns null when nothing was armed or nothing could open.</summary>
    public static AudioSidecarCapture? Start(string recordingPath, bool mic, string? micDeviceId,
        bool systemSound, Func<long?> captureTimeMs)
    {
        var capture = new AudioSidecarCapture();
        if (mic)
            capture.TryAdd(() => WasapiAudioSource.Microphone(micDeviceId),
                AppStorage.MicWavFor(recordingPath), captureTimeMs);
        if (systemSound)
            capture.TryAdd(() => WasapiAudioSource.SystemLoopback(),
                AppStorage.SystemWavFor(recordingPath), captureTimeMs);

        if (capture.IsCapturing) return capture;
        capture.Dispose();
        return null;
    }

    private void TryAdd(Func<IAudioSource> makeSource, string sidecarPath, Func<long?> clock)
    {
        IAudioSource? source = null;
        WavWriter? writer = null;
        try
        {
            source = makeSource();
            writer = new WavWriter(sidecarPath, source.Format);
            var recorder = new AudioCaptureRecorder(source, writer, clock);
            recorder.Start();
            _recorders.Add(recorder);
            _paths.Add(sidecarPath);
        }
        catch
        {
            // Device unavailable / in exclusive use / can't create the file — skip this source.
            try { writer?.Dispose(); } catch { /* ignore */ }
            try { source?.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>Stop capturing (no more samples written). Safe before <see cref="Dispose"/>.</summary>
    public void Stop()
    {
        foreach (var recorder in _recorders)
            try { recorder.Stop(); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        foreach (var recorder in _recorders)
            try { recorder.Dispose(); } catch { /* best effort — Dispose patches & closes each WAV */ }
        _recorders.Clear();
    }
}
