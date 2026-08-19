using System.Buffers.Binary;
using Shrike.Core.Audio;

namespace Shrike.Tests;

public class WavFileTests
{
    private static byte[] Ramp(int bytes)
    {
        var b = new byte[bytes];
        for (var i = 0; i < bytes; i++) b[i] = (byte)(i & 0xFF);
        return b;
    }

    [Fact]
    public void Write_then_read_round_trips_format_and_pcm()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shrike-wav-{Guid.NewGuid():N}.wav");
        try
        {
            var pcm = Ramp(4000);
            WavFile.Write(path, AudioFormat.Default, pcm);

            var (format, read) = WavFile.Read(path);
            Assert.Equal(AudioFormat.Default, format);
            Assert.Equal(pcm, read);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Streaming_writer_patches_sizes_on_dispose()
    {
        using var ms = new MemoryStream();
        var fmt = new AudioFormat(44_100, 1, 16);
        using (var w = new WavWriter(ms, fmt, ownsStream: false))
        {
            w.Write(Ramp(100));
            w.Write(Ramp(100));
            Assert.Equal(200, w.DataBytes);
        }

        // RIFF size at offset 4 = 36 + data; data size at offset 40 = 200.
        var bytes = ms.ToArray();
        Assert.Equal((uint)(36 + 200), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal((uint)200, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));

        ms.Position = 0;
        var (rf, pcm) = WavFile.Read(ms);
        Assert.Equal(fmt, rf);
        Assert.Equal(200, pcm.Length);
    }

    [Fact]
    public void Reading_a_non_riff_stream_throws()
    {
        using var ms = new MemoryStream(new byte[64]); // zeros, no RIFF magic
        Assert.Throws<InvalidDataException>(() => WavFile.Read(ms));
    }

    [Fact]
    public void Reader_skips_unknown_chunks_before_data()
    {
        using var ms = new MemoryStream();
        // Hand-craft: RIFF/WAVE, fmt, a bogus "LIST" chunk, then data.
        var pcm = Ramp(8);
        WriteHeaderWithExtraChunk(ms, AudioFormat.Default, pcm);
        ms.Position = 0;

        var (fmt, read) = WavFile.Read(ms);
        Assert.Equal(AudioFormat.Default, fmt);
        Assert.Equal(pcm, read);
    }

    private static void WriteHeaderWithExtraChunk(Stream s, AudioFormat f, byte[] pcm)
    {
        void Str(string v) => s.Write(System.Text.Encoding.ASCII.GetBytes(v));
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b); }
        void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); s.Write(b); }

        var list = new byte[6]; // arbitrary junk chunk payload
        Str("RIFF"); U32((uint)(4 + (8 + 16) + (8 + list.Length) + (8 + pcm.Length))); Str("WAVE");
        Str("fmt "); U32(16); U16(1); U16((ushort)f.Channels); U32((uint)f.SampleRate);
        U32((uint)f.BytesPerSecond); U16((ushort)f.BlockAlign); U16((ushort)f.BitsPerSample);
        Str("LIST"); U32((uint)list.Length); s.Write(list);
        Str("data"); U32((uint)pcm.Length); s.Write(pcm);
    }
}
