using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Shrike.Core.Audio;

namespace Shrike.Audio;

/// <summary>
/// NAudio implementation of <see cref="IAudioPlayer"/> for the mic-check "test &amp; play back" step (and, later,
/// editor preview). Plays a PCM WAV through NAudio 3's <c>WasapiPlayer</c>, resampling and re-channelling to
/// the render device's mix format so a 16 kHz headset clip still plays on a 48 kHz output. Single-clip,
/// main-thread use; not thread-safe.
/// </summary>
public sealed class NAudioPlayer : IAudioPlayer
{
    private WasapiPlayer? _player;
    private WaveFileReader? _reader;

    public TimeSpan Duration { get; private set; }

    public bool IsPlaying => _player?.PlaybackState == PlaybackState.Playing;

    public TimeSpan Position
    {
        get => _reader?.CurrentTime ?? TimeSpan.Zero;
        set { if (_reader is not null) _reader.CurrentTime = Clamp(value); }
    }

    public void Load(string wavPath)
    {
        Unload();

        var reader = new WaveFileReader(wavPath);
        var player = new WasapiPlayerBuilder().Build();

        // Match the render device's mix format: convert to float, resample, and fix channel count.
        ISampleProvider sample = reader.ToSampleProvider();
        var mix = player.DeviceMixFormat;
        if (sample.WaveFormat.SampleRate != mix.SampleRate)
            sample = new WdlResamplingSampleProvider(sample, mix.SampleRate);
        if (sample.WaveFormat.Channels != mix.Channels)
            sample = mix.Channels == 1 ? sample.ToMono() : sample.ToStereo();

        player.Init(sample.ToWaveProvider());

        _reader = reader;
        _player = player;
        Duration = reader.TotalTime;
    }

    public void Play() => _player?.Play();

    public void Pause() => _player?.Pause();

    public void Stop()
    {
        _player?.Stop();
        if (_reader is not null) _reader.Position = 0;
    }

    public void Dispose() => Unload();

    private void Unload()
    {
        _player?.Dispose();
        _reader?.Dispose();
        _player = null;
        _reader = null;
        Duration = TimeSpan.Zero;
    }

    private TimeSpan Clamp(TimeSpan t)
    {
        if (t < TimeSpan.Zero) return TimeSpan.Zero;
        return t > Duration ? Duration : t;
    }
}
