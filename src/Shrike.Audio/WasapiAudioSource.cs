using NAudio.CoreAudioApi;
using NAudio.Wave;
using Shrike.Core.Audio;

namespace Shrike.Audio;

/// <summary>
/// WASAPI implementation of <see cref="IAudioSource"/> for both the microphone (<see cref="Microphone"/>) and
/// system-sound loopback (<see cref="SystemLoopback"/>), built on NAudio 3's <c>WasapiRecorder</c>. It
/// captures in the device's shared-mode format (usually 32-bit float), converts each zero-copy buffer to
/// 16-bit PCM via Core's <see cref="Pcm16Converter"/>, and re-raises it as <see cref="DataAvailable"/> on the
/// capture thread. Sample rate and channel count are left as the device reports them — ffmpeg resamples/mixes
/// at export, so no resampler is needed here.
/// </summary>
public sealed class WasapiAudioSource : IAudioSource
{
    private readonly WasapiRecorder _recorder;
    private readonly bool _isFloat;
    private readonly int _bits;
    private bool _running;
    private bool _disposed;

    public AudioFormat Format { get; }

    public event Action<byte[]>? DataAvailable;

    private WasapiAudioSource(WasapiRecorder recorder)
    {
        _recorder = recorder;
        // Shared-mode WASAPI reports its mix format as WAVE_FORMAT_EXTENSIBLE, whose Encoding is `Extensible`
        // (not `IeeeFloat`/`Pcm`) and hides the real sample type in a sub-format GUID. Resolve it to a plain
        // WaveFormat first — otherwise 32-bit float capture is misread as 32-bit integer PCM and the top bytes
        // of each float (its sign/exponent) are written as samples, i.e. loud garbage.
        var wf = recorder.WaveFormat is WaveFormatExtensible ext ? ext.ToStandardWaveFormat() : recorder.WaveFormat;
        _isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat;
        _bits = wf.BitsPerSample;
        Format = new AudioFormat(wf.SampleRate, wf.Channels, 16);
        _recorder.DataAvailable += OnData;
    }

    /// <summary>Capture from a microphone endpoint. Null selects the system default capture device.</summary>
    public static WasapiAudioSource Microphone(string? deviceId = null) =>
        new(BuildRecorder(new WasapiRecorderBuilder(), deviceId));

    /// <summary>Capture what the machine is playing (loopback on a render endpoint). Null selects the default
    /// render device.</summary>
    public static WasapiAudioSource SystemLoopback(string? renderDeviceId = null) =>
        new(BuildRecorder(new WasapiRecorderBuilder().WithLoopbackCapture(), renderDeviceId));

    private static WasapiRecorder BuildRecorder(WasapiRecorderBuilder builder, string? deviceId)
    {
        if (deviceId is not null)
        {
            using var enumerator = new MMDeviceEnumerator();
            builder = builder.WithDevice(enumerator.GetDevice(deviceId));
        }
        return builder.Build();
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (buffer.IsEmpty) return;
        var pcm = Pcm16Converter.To16Bit(buffer, _bits, _isFloat);
        DataAvailable?.Invoke(pcm);
    }

    public void Start()
    {
        if (_running || _disposed) return;
        _running = true;
        _recorder.StartRecording();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _recorder.StopRecording();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recorder.DataAvailable -= OnData;
        try { _recorder.StopRecording(); } catch { /* already stopped */ }
        _recorder.Dispose();
    }
}
