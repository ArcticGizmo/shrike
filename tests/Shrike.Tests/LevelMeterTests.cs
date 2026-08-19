using System.Buffers.Binary;
using Shrike.Core.Audio;

namespace Shrike.Tests;

public class LevelMeterTests
{
    private static byte[] Pcm16(params short[] samples)
    {
        var b = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), samples[i]);
        return b;
    }

    [Fact]
    public void Empty_buffer_is_silent()
    {
        var level = LevelMeter.Measure([]);
        Assert.Equal(AudioLevel.Silent, level);
        Assert.Equal(AudioLevel.FloorDb, level.PeakDb);
    }

    [Fact]
    public void Silence_reads_at_the_floor()
    {
        var level = LevelMeter.Measure(Pcm16(0, 0, 0, 0));
        Assert.Equal(0, level.Peak);
        Assert.Equal(AudioLevel.FloorDb, level.RmsDb);
        Assert.False(level.Clipping);
    }

    [Fact]
    public void Full_scale_reads_zero_dbfs_and_clips()
    {
        var level = LevelMeter.Measure(Pcm16(short.MaxValue, short.MinValue));
        Assert.True(level.Peak >= 0.999);
        Assert.True(level.Clipping);
        Assert.InRange(level.PeakDb, -0.1, 0.0);
    }

    [Fact]
    public void Half_scale_peak_is_about_minus_six_dbfs()
    {
        var level = LevelMeter.Measure(Pcm16(16_384, -16_384)); // 32768/2
        Assert.InRange(level.PeakDb, -6.1, -5.9);
        Assert.False(level.Clipping);
    }

    [Fact]
    public void Rms_of_constant_amplitude_equals_that_amplitude()
    {
        // All samples at half scale -> RMS == peak == 0.5.
        var level = LevelMeter.Measure(Pcm16(16_384, 16_384, 16_384, 16_384));
        Assert.InRange(level.Rms, 0.49, 0.51);
    }
}
