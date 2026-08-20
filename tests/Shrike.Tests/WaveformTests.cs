using System.Buffers.Binary;
using Shrike.Core.Audio;

namespace Shrike.Tests;

public class WaveformTests
{
    private static byte[] Const(short amp, int samples)
    {
        var b = new byte[samples * 2];
        for (var i = 0; i < samples; i++) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), amp);
        return b;
    }

    [Fact]
    public void Empty_buffer_yields_zero_peaks()
    {
        var peaks = Waveform.ComputePeaks([], 8);
        Assert.Equal(8, peaks.Length);
        Assert.All(peaks, p => Assert.Equal(0f, p));
    }

    [Fact]
    public void Full_scale_reads_near_one()
    {
        var peaks = Waveform.ComputePeaks(Const(short.MaxValue, 100), 10);
        Assert.All(peaks, p => Assert.True(p > 0.99f));
    }

    [Fact]
    public void Half_scale_reads_about_half()
    {
        var peaks = Waveform.ComputePeaks(Const(16_384, 100), 4);
        Assert.All(peaks, p => Assert.InRange(p, 0.49f, 0.51f));
    }

    [Fact]
    public void Bucket_count_is_honoured()
    {
        Assert.Equal(37, Waveform.ComputePeaks(Const(1000, 500), 37).Length);
        Assert.Single(Waveform.ComputePeaks(Const(1000, 500), 0)); // clamped to at least one
    }

    [Fact]
    public void A_loud_slice_only_lifts_its_own_bucket()
    {
        // First half silent, second half full-scale -> two buckets: ~0 then ~1.
        var pcm = new byte[200 * 2];
        for (var i = 100; i < 200; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), short.MaxValue);
        var peaks = Waveform.ComputePeaks(pcm, 2);
        Assert.Equal(0f, peaks[0]);
        Assert.True(peaks[1] > 0.99f);
    }
}
