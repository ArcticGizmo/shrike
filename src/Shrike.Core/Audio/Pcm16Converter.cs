using System.Buffers.Binary;

namespace Shrike.Core.Audio;

/// <summary>
/// Converts a capture device's native buffer to signed 16-bit little-endian PCM — the one format the rest of
/// Core stores and the sidecar WAV holds. WASAPI shared mode usually hands us 32-bit IEEE float; some devices
/// give 16/24/32-bit integer PCM. Resampling and channel-mixing are deliberately <b>not</b> done here: the
/// sidecar keeps the device's sample rate and channel count, and ffmpeg resamples/mixes once at export.
/// Pure byte maths (no NAudio) so it lives in Core and is unit-tested.
/// </summary>
public static class Pcm16Converter
{
    /// <summary>Convert <paramref name="src"/> (a whole number of samples in the given encoding) to 16-bit PCM.
    /// 16-bit input is copied; 24/32-bit integer input is truncated to the top 16 bits; 32-bit float is scaled
    /// and clamped.</summary>
    public static byte[] To16Bit(ReadOnlySpan<byte> src, int bitsPerSample, bool isFloat)
    {
        if (isFloat)
        {
            if (bitsPerSample != 32) throw new NotSupportedException($"Float{bitsPerSample} not supported.");
            return FromFloat32(src);
        }

        return bitsPerSample switch
        {
            16 => src.ToArray(),          // already the target format
            24 => FromInt24(src),
            32 => FromInt32(src),
            _ => throw new NotSupportedException($"PCM{bitsPerSample} not supported."),
        };
    }

    private static byte[] FromFloat32(ReadOnlySpan<byte> src)
    {
        var samples = src.Length / 4;
        var outBuf = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var f = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(i * 4, 4));
            var s = ClampToInt16(f * 32768.0f);
            BinaryPrimitives.WriteInt16LittleEndian(outBuf.AsSpan(i * 2, 2), s);
        }
        return outBuf;
    }

    private static byte[] FromInt24(ReadOnlySpan<byte> src)
    {
        var samples = src.Length / 3;
        var outBuf = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            // Drop the least-significant byte: the top two bytes of a 24-bit LE sample are its 16-bit form.
            outBuf[i * 2] = src[i * 3 + 1];
            outBuf[i * 2 + 1] = src[i * 3 + 2];
        }
        return outBuf;
    }

    private static byte[] FromInt32(ReadOnlySpan<byte> src)
    {
        var samples = src.Length / 4;
        var outBuf = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            // Take the top 16 bits of each 32-bit LE sample.
            outBuf[i * 2] = src[i * 4 + 2];
            outBuf[i * 2 + 1] = src[i * 4 + 3];
        }
        return outBuf;
    }

    private static short ClampToInt16(float scaled)
    {
        if (scaled >= short.MaxValue) return short.MaxValue;
        if (scaled <= short.MinValue) return short.MinValue;
        return (short)MathF.Round(scaled);
    }
}
