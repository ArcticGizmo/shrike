using System.Buffers.Binary;
using Shrike.Audio;
using Shrike.Core.Audio;

namespace Shrike.Tests;

public class AudioLevelMonitorTests
{
    private sealed class FakeAudioSource : IAudioSource
    {
        public AudioFormat Format { get; init; } = AudioFormat.Default;
        public bool Started;
        public bool Disposed;
        public event Action<byte[]>? DataAvailable;
        public void Start() => Started = true;
        public void Stop() => Started = false;
        public void Emit(byte[] pcm) => DataAvailable?.Invoke(pcm);
        public void Dispose() => Disposed = true;
    }

    private static byte[] Tone(short amp, int samples = 64)
    {
        var b = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), (short)(i % 2 == 0 ? amp : -amp));
        return b;
    }

    [Fact]
    public void Reads_the_peak_since_last_poll()
    {
        var src = new FakeAudioSource();
        using var mon = new AudioLevelMonitor(src);
        mon.Start();
        Assert.True(src.Started);

        src.Emit(Tone(8_192));       // quiet
        src.Emit(Tone(short.MaxValue)); // loud transient between polls
        var level = mon.ReadAndDecay();

        Assert.True(level.Peak >= 0.999); // the loud transient is caught
    }

    [Fact]
    public void Peak_decays_after_reading()
    {
        var src = new FakeAudioSource();
        using var mon = new AudioLevelMonitor(src);
        mon.Start();

        src.Emit(Tone(short.MaxValue));
        Assert.True(mon.ReadAndDecay().Peak >= 0.999);

        // No new audio → the held peak has fallen back.
        Assert.True(mon.ReadAndDecay().Peak < 0.5);
    }

    [Fact]
    public void Dispose_stops_and_disposes_the_source()
    {
        var src = new FakeAudioSource();
        var mon = new AudioLevelMonitor(src);
        mon.Start();
        mon.Dispose();
        Assert.True(src.Disposed);
        Assert.False(src.Started);
    }
}
