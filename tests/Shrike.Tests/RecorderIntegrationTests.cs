using System.Buffers.Binary;
using System.Text;
using Shrike.Core.Capture;
using Shrike.Core.Recording;

namespace Shrike.Tests;

/// <summary>
/// The M4.2 exit criterion end-to-end: capture a real screen region through the GDI source + FFmpeg
/// encoder and confirm a playable MP4 lands on disk. Needs both a desktop and ffmpeg; no-ops otherwise.
/// </summary>
public class RecorderIntegrationTests
{
    [Fact]
    public void Records_a_region_to_a_playable_mp4()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg) return;
        var vs = ScreenCapture.VirtualScreenBounds();
        if (vs.IsEmpty) return;

        var region = new PixelBounds(vs.X, vs.Y, Math.Min(320, vs.Width), Math.Min(240, vs.Height));
        var src = new GdiFrameSource(region);
        var path = Path.Combine(Path.GetTempPath(), $"shrike-rec-{Guid.NewGuid():N}.mp4");
        var enc = new FfmpegMp4Encoder(ffmpeg, path, src.Width, src.Height, fps: 30, bitrate: 4_000_000);
        var rec = new Recorder(src, enc, path, fps: 30);
        try
        {
            rec.Start();
            Thread.Sleep(500);          // ~half a second of real capture
            Assert.True(rec.Elapsed > TimeSpan.Zero);
            rec.Stop();
            rec.Dispose();

            Assert.True(File.Exists(path), "no output file");
            var data = File.ReadAllBytes(path);
            Assert.True(data.Length > 2_000, $"file suspiciously small: {data.Length} bytes");
            Assert.Equal("ftyp", Encoding.ASCII.GetString(data, 4, 4));
            Assert.True(FindBox(data, "moov"), "no moov box — container not finalised");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static bool FindBox(byte[] data, string fourcc)
    {
        var pos = 0;
        while (pos + 8 <= data.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
            if (Encoding.ASCII.GetString(data, pos + 4, 4) == fourcc) return true;
            if (size < 8) break;
            pos += (int)size;
        }
        return false;
    }
}
