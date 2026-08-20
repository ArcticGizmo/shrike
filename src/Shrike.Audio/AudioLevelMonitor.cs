using Shrike.Core.Audio;

namespace Shrike.Audio;

/// <summary>
/// Drives the mic-check level meter: opens an <see cref="IAudioSource"/> purely to measure it (nothing is
/// written), computing RMS/peak on the capture thread via Core's <see cref="LevelMeter"/> and holding the
/// latest reading for the UI to poll. <see cref="ReadAndDecay"/> returns the loudest peak seen since the last
/// call, so a UI timer sampling at ~4–30 Hz still catches brief transients between polls. Owns the source and
/// disposes it. Thread-safe: the audio callback writes under a lock; the UI reads under the same lock.
/// </summary>
public sealed class AudioLevelMonitor : IDisposable
{
    private readonly IAudioSource _source;
    private readonly object _lock = new();
    private double _peak;
    private double _rms;
    private bool _started;
    private bool _disposed;

    public AudioLevelMonitor(IAudioSource source) => _source = source ?? throw new ArgumentNullException(nameof(source));

    public AudioFormat Format => _source.Format;

    public void Start()
    {
        lock (_lock) { if (_started || _disposed) return; _started = true; }
        _source.DataAvailable += OnData;
        _source.Start();
    }

    private void OnData(byte[] pcm)
    {
        var level = LevelMeter.Measure(pcm);
        lock (_lock)
        {
            if (level.Peak > _peak) _peak = level.Peak; // hold the loudest peak until read
            _rms = level.Rms;
        }
    }

    /// <summary>The loudest peak (and most recent RMS) seen since the last call, then reset the hold so the
    /// next reading reflects only audio captured since — and the meter falls to zero when input stops.</summary>
    public AudioLevel ReadAndDecay()
    {
        lock (_lock)
        {
            var level = new AudioLevel(_rms, _peak);
            _peak = 0;
            _rms = 0;
            return level;
        }
    }

    public void Stop()
    {
        _source.DataAvailable -= OnData;
        _source.Stop();
    }

    public void Dispose()
    {
        lock (_lock) { if (_disposed) return; _disposed = true; }
        Stop();
        _source.Dispose();
    }
}
