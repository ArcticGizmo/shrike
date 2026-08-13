using System.Buffers.Binary;
using Shrike.Core.Recording;

namespace Shrike.Tests;

/// <summary>
/// End-to-end encoder tests. These need a real ffmpeg on the machine; when none is found they no-op so
/// the suite stays green (CI/dev without ffmpeg), and do their real assertions where ffmpeg exists.
/// </summary>
public class FfmpegMp4EncoderTests
{
    private const int Fps = 30;

    private static byte[] Frame(int w, int h, byte b, byte g, byte r)
    {
        var buf = new byte[w * h * 4];
        for (var i = 0; i < buf.Length; i += 4)
        {
            buf[i + 0] = b; buf[i + 1] = g; buf[i + 2] = r; buf[i + 3] = 255;
        }
        return buf;
    }

    [Fact]
    public void Encodes_a_wellformed_mp4()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg)
            return; // no ffmpeg here — see class summary.

        var path = Path.Combine(Path.GetTempPath(), $"shrike-ff-{Guid.NewGuid():N}.mp4");
        const int w = 320, h = 240, frames = 30;
        try
        {
            using (var enc = new FfmpegMp4Encoder(ffmpeg, path, w, h, Fps, bitrate: 2_000_000))
            {
                for (var i = 0; i < frames; i++)
                    enc.WriteFrame(Frame(w, h, (byte)(i * 8), (byte)(255 - i * 8), 64));
                enc.Finish();
            }

            Assert.True(File.Exists(path), "encoder produced no file");
            var data = File.ReadAllBytes(path);
            Assert.True(data.Length > 2_000, $"file suspiciously small: {data.Length} bytes");
            Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(data, 4, 4));
            Assert.True(FindBox(data, "moov"), "no moov box — container not finalised");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Rejects_a_wrongly_sized_frame()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg)
            return;

        var path = Path.Combine(Path.GetTempPath(), $"shrike-ff-{Guid.NewGuid():N}.mp4");
        try
        {
            using var enc = new FfmpegMp4Encoder(ffmpeg, path, 64, 64, Fps, bitrate: 500_000);
            Assert.Throws<ArgumentException>(() => enc.WriteFrame(new byte[10]));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Sustains_1080p_throughput()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg)
            return;

        // Throughput smoke test (exit criterion): push 2s of 1080p and confirm it finishes well inside
        // a generous budget and yields a valid file. Reuses one buffer so we measure encode, not alloc.
        var path = Path.Combine(Path.GetTempPath(), $"shrike-ff-{Guid.NewGuid():N}.mp4");
        const int w = 1920, h = 1080, frames = 60;
        var frame = Frame(w, h, 40, 90, 140);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using (var enc = new FfmpegMp4Encoder(ffmpeg, path, w, h, Fps, bitrate: 8_000_000))
            {
                for (var i = 0; i < frames; i++)
                    enc.WriteFrame(frame);
                enc.Finish();
            }
            sw.Stop();

            Assert.True(File.Exists(path) && new FileInfo(path).Length > 2_000, "no/short output");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"1080p encode too slow: {sw.Elapsed}");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Rejects_odd_dimensions()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg)
            return;

        var path = Path.Combine(Path.GetTempPath(), $"shrike-ff-{Guid.NewGuid():N}.mp4");
        Assert.Throws<ArgumentException>(() => new FfmpegMp4Encoder(ffmpeg, path, 101, 100, Fps, 500_000));
    }

    // Walk the top-level MP4 box list looking for the given type.
    private static bool FindBox(byte[] data, string fourcc)
    {
        var pos = 0;
        while (pos + 8 <= data.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
            if (System.Text.Encoding.ASCII.GetString(data, pos + 4, 4) == fourcc) return true;
            if (size < 8) break;
            pos += (int)size;
        }
        return false;
    }
}
