using Shrike.Core.Audio;

namespace Shrike.Tests;

public class AudioFormatTests
{
    [Fact]
    public void Default_is_48k_stereo_16bit()
    {
        var f = AudioFormat.Default;
        Assert.Equal(48_000, f.SampleRate);
        Assert.Equal(2, f.Channels);
        Assert.Equal(16, f.BitsPerSample);
        Assert.Equal(4, f.BlockAlign);          // 2 ch * 2 bytes
        Assert.Equal(192_000, f.BytesPerSecond); // 48000 * 4
        Assert.True(f.IsPcm16);
    }

    [Fact]
    public void Bytes_and_ms_round_trip_on_block_boundaries()
    {
        var f = AudioFormat.Default;
        Assert.Equal(192_000, f.MsToBytes(1000));
        Assert.Equal(1000, f.BytesToMs(192_000));
    }

    [Fact]
    public void MsToBytes_is_block_aligned()
    {
        // 48kHz stereo 16-bit: 1ms = 192 bytes, already aligned; use a mono odd-rate to force rounding.
        var mono = new AudioFormat(44_100, 1, 16); // block align 2
        var bytes = mono.MsToBytes(1); // 44100*2/1000 = 88.2 -> 88, aligned to 2 = 88
        Assert.Equal(0, bytes % mono.BlockAlign);
        Assert.Equal(88, bytes);
    }

    [Fact]
    public void Zero_and_negative_durations_are_empty()
    {
        var f = AudioFormat.Default;
        Assert.Equal(0, f.MsToBytes(0));
        Assert.Equal(0, f.MsToBytes(-50));
    }
}
