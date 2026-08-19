using System.Buffers.Binary;

namespace Shrike.Core.Audio;

/// <summary>
/// Reduces a PCM buffer to a small array of peak amplitudes (0..1) — the shape the editor's waveform lane
/// draws. Channels are folded together (the loudest sample in a bucket wins), so a stereo sidecar still
/// renders one waveform. Pure and deterministic; the editor reads a sidecar once, decimates to as many
/// buckets as the lane is pixels wide, and caches the result.
/// </summary>
public static class Waveform
{
    /// <summary>Peak amplitude (0..1) for each of <paramref name="buckets"/> equal slices of a 16-bit PCM
    /// buffer. An empty buffer yields all-zero peaks.</summary>
    public static float[] ComputePeaks(ReadOnlySpan<byte> pcm16le, int buckets)
    {
        var peaks = new float[Math.Max(1, buckets)];
        var sampleCount = pcm16le.Length / 2;
        if (sampleCount == 0) return peaks;

        for (var b = 0; b < peaks.Length; b++)
        {
            var start = (int)((long)b * sampleCount / peaks.Length);
            var end = (int)((long)(b + 1) * sampleCount / peaks.Length);
            double max = 0;
            for (var i = start; i < end; i++)
            {
                var s = BinaryPrimitives.ReadInt16LittleEndian(pcm16le.Slice(i * 2, 2));
                var amp = s == short.MinValue ? 1.0 : Math.Abs(s) / 32768.0;
                if (amp > max) max = amp;
            }
            peaks[b] = (float)max;
        }
        return peaks;
    }
}
