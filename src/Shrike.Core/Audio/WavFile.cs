using System.Buffers.Binary;

namespace Shrike.Core.Audio;

/// <summary>
/// Canonical PCM WAV read/write — the on-disk shape of an audio sidecar. Deliberately tiny and
/// dependency-free (no NAudio in Core): a 44-byte RIFF/WAVE header plus interleaved PCM. <see cref="WavWriter"/>
/// streams during capture and patches the length fields on close; <see cref="Read"/> loads a whole file for
/// tests and waveform decimation. Only integer PCM is written; the reader tolerates extra chunks by scanning.
/// </summary>
public static class WavFile
{
    /// <summary>Write a complete PCM WAV file in one shot.</summary>
    public static void Write(string path, AudioFormat format, ReadOnlySpan<byte> pcm)
    {
        using var w = new WavWriter(path, format);
        w.Write(pcm);
    }

    /// <summary>Read a PCM WAV file, returning its format and the raw interleaved PCM bytes.</summary>
    public static (AudioFormat Format, byte[] Pcm) Read(string path)
    {
        using var s = File.OpenRead(path);
        return Read(s);
    }

    /// <summary>Read just the format and PCM byte length without loading the samples — cheap for a long
    /// recording's sidecar when only the duration/format is needed (the data chunk is skipped, not read).</summary>
    public static (AudioFormat Format, long DataBytes) ReadInfo(string path)
    {
        using var s = File.OpenRead(path);
        return ReadInfo(s);
    }

    /// <summary>Read format + PCM length from a stream positioned at the RIFF header, without reading the PCM.</summary>
    public static (AudioFormat Format, long DataBytes) ReadInfo(Stream stream)
    {
        Span<byte> hdr = stackalloc byte[12];
        ReadExactly(stream, hdr);
        if (hdr[0] != 'R' || hdr[1] != 'I' || hdr[2] != 'F' || hdr[3] != 'F' ||
            hdr[8] != 'W' || hdr[9] != 'A' || hdr[10] != 'V' || hdr[11] != 'E')
            throw new InvalidDataException("Not a RIFF/WAVE stream.");

        AudioFormat? format = null;
        Span<byte> chunkHdr = stackalloc byte[8];
        while (TryReadExactly(stream, chunkHdr))
        {
            var id = System.Text.Encoding.ASCII.GetString(chunkHdr[..4]);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunkHdr[4..]);

            if (id == "fmt ")
            {
                var fmt = new byte[size];
                ReadExactly(stream, fmt);
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                var sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                format = new AudioFormat(sampleRate, channels, bits);
                if (size % 2 == 1) stream.Seek(1, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                if (format is not { } f) throw new InvalidDataException("WAV data before fmt.");
                return (f, size); // stop here — don't read the samples
            }
            else
            {
                stream.Seek(size + (size % 2), SeekOrigin.Current);
            }
        }

        if (format is not { } fmt2) throw new InvalidDataException("WAV missing fmt chunk.");
        return (fmt2, 0);
    }

    /// <summary>Read PCM WAV from a stream positioned at the RIFF header.</summary>
    public static (AudioFormat Format, byte[] Pcm) Read(Stream stream)
    {
        Span<byte> hdr = stackalloc byte[12];
        ReadExactly(stream, hdr);
        if (hdr[0] != 'R' || hdr[1] != 'I' || hdr[2] != 'F' || hdr[3] != 'F' ||
            hdr[8] != 'W' || hdr[9] != 'A' || hdr[10] != 'V' || hdr[11] != 'E')
            throw new InvalidDataException("Not a RIFF/WAVE stream.");

        AudioFormat? format = null;
        byte[]? pcm = null;

        Span<byte> chunkHdr = stackalloc byte[8];
        while (TryReadExactly(stream, chunkHdr))
        {
            var id = System.Text.Encoding.ASCII.GetString(chunkHdr[..4]);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunkHdr[4..]);

            if (id == "fmt ")
            {
                var fmt = new byte[size];
                ReadExactly(stream, fmt);
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                var sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                format = new AudioFormat(sampleRate, channels, bits);
                if (size % 2 == 1) stream.Seek(1, SeekOrigin.Current); // word-align padding
            }
            else if (id == "data")
            {
                pcm = new byte[size];
                ReadExactly(stream, pcm);
                if (size % 2 == 1) stream.Seek(1, SeekOrigin.Current);
            }
            else
            {
                stream.Seek(size + (size % 2), SeekOrigin.Current); // skip unknown chunk (+ pad)
            }
        }

        if (format is not { } f) throw new InvalidDataException("WAV missing fmt chunk.");
        return (f, pcm ?? []);
    }

    private static void ReadExactly(Stream s, Span<byte> buffer)
    {
        if (!TryReadExactly(s, buffer)) throw new EndOfStreamException();
    }

    private static bool TryReadExactly(Stream s, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = s.Read(buffer[read..]);
            if (n == 0) return read == 0 ? false : throw new EndOfStreamException();
            read += n;
        }
        return true;
    }
}

/// <summary>
/// Streams PCM into a WAV file during capture. Writes a placeholder header up front, appends buffers as they
/// arrive, then patches the RIFF and data sizes on <see cref="Dispose"/>. The stream must be seekable
/// (a real file is). Not thread-safe — feed it from one writer.
/// </summary>
public sealed class WavWriter : IDisposable
{
    private const int HeaderSize = 44;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private long _dataBytes;
    private bool _disposed;

    public WavWriter(string path, AudioFormat format)
        : this(File.Create(path), format, ownsStream: true) { }

    public WavWriter(Stream stream, AudioFormat format, bool ownsStream = false)
    {
        if (!stream.CanSeek) throw new ArgumentException("WavWriter needs a seekable stream.", nameof(stream));
        _stream = stream;
        _ownsStream = ownsStream;
        Format = format;
        WriteHeader(dataBytes: 0); // placeholder; patched on dispose
    }

    public AudioFormat Format { get; }

    /// <summary>Total PCM bytes written so far.</summary>
    public long DataBytes => _dataBytes;

    /// <summary>Append PCM in <see cref="Format"/>. Bytes are written verbatim.</summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Write(pcm);
        _dataBytes += pcm.Length;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Seek(0, SeekOrigin.Begin);
        WriteHeader(_dataBytes); // patch sizes
        _stream.Flush();
        if (_ownsStream) _stream.Dispose();
    }

    private void WriteHeader(long dataBytes)
    {
        Span<byte> h = stackalloc byte[HeaderSize];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);              // PCM fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1);               // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], (ushort)Format.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)Format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)Format.BytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)Format.BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], (ushort)Format.BitsPerSample);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], (uint)dataBytes);
        _stream.Write(h);
    }
}
