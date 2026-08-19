using System.Buffers.Binary;

namespace Shrike.Core.Audio;

/// <summary>A measured audio level as linear amplitudes in 0..1, with dBFS accessors for the meter UI.</summary>
public readonly record struct AudioLevel(double Rms, double Peak)
{
    /// <summary>Floor reported for silence, in dBFS. Below this the log would run to negative infinity.</summary>
    public const double FloorDb = -90.0;

    /// <summary>RMS level in dBFS (0 dB = full scale), clamped at <see cref="FloorDb"/>.</summary>
    public double RmsDb => ToDb(Rms);

    /// <summary>Peak level in dBFS (0 dB = full scale), clamped at <see cref="FloorDb"/>.</summary>
    public double PeakDb => ToDb(Peak);

    /// <summary>True when the peak reached digital full scale (clipping).</summary>
    public bool Clipping => Peak >= 0.999;

    /// <summary>Nothing measured — silence.</summary>
    public static readonly AudioLevel Silent = new(0, 0);

    private static double ToDb(double amp) => amp <= 1e-6 ? FloorDb : Math.Max(FloorDb, 20.0 * Math.Log10(amp));
}

/// <summary>
/// Computes RMS and peak levels from a signed 16-bit PCM buffer — the maths behind the mic-check meter and
/// recording-HUD level. Pure and deterministic (no device, no state), so it is fully unit-tested; the live
/// UI feeds it capture buffers and applies its own decay ballistics on top.
/// </summary>
public static class LevelMeter
{
    /// <summary>Measure the level of a 16-bit little-endian PCM buffer. Channels are folded together (the
    /// loudest channel drives peak; RMS is over all samples). A buffer shorter than one sample reads as
    /// silent.</summary>
    public static AudioLevel Measure(ReadOnlySpan<byte> pcm16le)
    {
        var sampleCount = pcm16le.Length / 2;
        if (sampleCount == 0) return AudioLevel.Silent;

        double peak = 0;
        double sumSquares = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var s = BinaryPrimitives.ReadInt16LittleEndian(pcm16le.Slice(i * 2, 2));
            // -32768 has no positive twin; treat it as full-scale 1.0 rather than 1.0000305.
            var amp = s == short.MinValue ? 1.0 : Math.Abs(s) / 32768.0;
            if (amp > peak) peak = amp;
            sumSquares += amp * amp;
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);
        return new AudioLevel(rms, peak);
    }
}
