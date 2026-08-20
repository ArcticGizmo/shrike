using System.Buffers.Binary;
using Shrike.Core.Audio;

namespace Shrike.Tests;

public class Pcm16ConverterTests
{
    private static byte[] Floats(params float[] values)
    {
        var b = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), values[i]);
        return b;
    }

    private static short[] AsInt16(byte[] pcm)
    {
        var s = new short[pcm.Length / 2];
        for (var i = 0; i < s.Length; i++) s[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
        return s;
    }

    [Fact]
    public void Float32_maps_full_scale_and_zero()
    {
        var pcm = Pcm16Converter.To16Bit(Floats(1.0f, 0f, -1.0f), bitsPerSample: 32, isFloat: true);
        var s = AsInt16(pcm);
        Assert.Equal(short.MaxValue, s[0]);
        Assert.Equal(0, s[1]);
        Assert.Equal(short.MinValue, s[2]);
    }

    [Fact]
    public void Float32_clamps_out_of_range_input()
    {
        var pcm = Pcm16Converter.To16Bit(Floats(2.5f, -3.0f), bitsPerSample: 32, isFloat: true);
        var s = AsInt16(pcm);
        Assert.Equal(short.MaxValue, s[0]);
        Assert.Equal(short.MinValue, s[1]);
    }

    [Fact]
    public void Float32_half_scale_is_about_a_quarter_of_max()
    {
        var pcm = Pcm16Converter.To16Bit(Floats(0.5f), bitsPerSample: 32, isFloat: true);
        var s = AsInt16(pcm);
        Assert.InRange(s[0], 16_380, 16_390); // 0.5 * 32768 = 16384
    }

    [Fact]
    public void Sixteen_bit_pcm_is_copied_verbatim()
    {
        var src = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var pcm = Pcm16Converter.To16Bit(src, bitsPerSample: 16, isFloat: false);
        Assert.Equal(src, pcm);
    }

    [Fact]
    public void TwentyFour_bit_pcm_takes_the_top_two_bytes()
    {
        // One 24-bit LE sample 0xAABBCC -> 16-bit 0xAABB (bytes BB, AA at output).
        var src = new byte[] { 0xCC, 0xBB, 0xAA };
        var pcm = Pcm16Converter.To16Bit(src, bitsPerSample: 24, isFloat: false);
        Assert.Equal(new byte[] { 0xBB, 0xAA }, pcm);
    }

    [Fact]
    public void ThirtyTwo_bit_int_pcm_takes_the_top_two_bytes()
    {
        var src = new byte[] { 0x11, 0x22, 0x33, 0x44 }; // -> 0x33, 0x44
        var pcm = Pcm16Converter.To16Bit(src, bitsPerSample: 32, isFloat: false);
        Assert.Equal(new byte[] { 0x33, 0x44 }, pcm);
    }

    [Fact]
    public void Unsupported_encoding_throws()
    {
        Assert.Throws<NotSupportedException>(() => Pcm16Converter.To16Bit(new byte[8], 8, isFloat: false));
        Assert.Throws<NotSupportedException>(() => Pcm16Converter.To16Bit(new byte[8], 64, isFloat: true));
    }
}
