using System.Buffers.Binary;
using Shrike.Core.Audio;

namespace Shrike.Tests;

public class AudioSpliceTests
{
    // Mono 16-bit at 1000 Hz so 1ms == 1 sample == 2 bytes — makes the ms/byte maths easy to reason about.
    private static readonly AudioFormat Fmt = new(1000, 1, 16);

    private static byte[] Const(short value, int samples)
    {
        var b = new byte[samples * 2];
        for (var i = 0; i < samples; i++) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), value);
        return b;
    }

    private static short SampleAt(byte[] pcm, int i) => BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));

    [Fact]
    public void Replaced_span_preserves_total_length()
    {
        var original = Const(1000, 100);            // 100ms
        var insert = Const(-1000, 40);              // 40ms
        var result = AudioSplice.Replace(original, Fmt, 30, 70, insert, fadeMs: 0);
        Assert.Equal(original.Length, result.Length); // length unchanged
    }

    [Fact]
    public void Span_is_replaced_and_surroundings_kept()
    {
        var original = Const(1000, 100);
        var insert = Const(-1000, 40);
        var result = AudioSplice.Replace(original, Fmt, 30, 70, insert, fadeMs: 0);

        Assert.Equal(1000, SampleAt(result, 10));   // before the span — untouched
        Assert.Equal(-1000, SampleAt(result, 50));  // inside the span — from the insert
        Assert.Equal(1000, SampleAt(result, 90));   // after the span — untouched
    }

    [Fact]
    public void A_short_insert_is_silence_padded_to_fill_the_span()
    {
        var original = Const(1000, 100);
        var insert = Const(-1000, 10);              // only 10ms for a 40ms span
        var result = AudioSplice.Replace(original, Fmt, 30, 70, insert, fadeMs: 0);
        Assert.Equal(0, SampleAt(result, 65));      // tail of the span padded with silence
    }

    [Fact]
    public void A_long_insert_is_truncated_to_the_span()
    {
        var original = Const(1000, 100);
        var insert = Const(-1000, 999);             // far longer than the 40ms span
        var result = AudioSplice.Replace(original, Fmt, 30, 70, insert, fadeMs: 0);
        Assert.Equal(original.Length, result.Length);
        Assert.Equal(1000, SampleAt(result, 90));   // after-span audio survives (insert didn't overrun)
    }

    [Fact]
    public void Fade_attenuates_the_insert_edges()
    {
        var original = Const(1000, 100);
        var insert = Const(-1000, 40);
        var result = AudioSplice.Replace(original, Fmt, 30, 70, insert, fadeMs: 5);
        // First sample of the insert is fully faded to zero; the middle is full amplitude.
        Assert.Equal(0, SampleAt(result, 30));
        Assert.Equal(-1000, SampleAt(result, 50));
    }

    [Fact]
    public void Empty_span_returns_the_original()
    {
        var original = Const(1000, 100);
        var result = AudioSplice.Replace(original, Fmt, 50, 50, Const(-1000, 10));
        Assert.Equal(original, result);
    }
}
