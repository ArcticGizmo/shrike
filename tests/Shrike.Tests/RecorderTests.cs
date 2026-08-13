using Shrike.Core.Recording;

namespace Shrike.Tests;

/// <summary>
/// Drives the threaded <see cref="Recorder"/> with fakes (no screen, no ffmpeg) to verify lifecycle and
/// that frames flow while recording, stop while paused, and the file is dropped on discard. Timing is
/// real wall-clock, so assertions use generous ranges rather than exact frame counts.
/// </summary>
public class RecorderTests
{
    private sealed class FakeSource : IFrameSource
    {
        private readonly byte[] _frame;
        public int Width { get; }
        public int Height { get; }
        public volatile int Captures;
        public FakeSource(int w, int h) { Width = w; Height = h; _frame = new byte[w * h * 4]; }
        public byte[] CaptureFrame() { Captures++; return _frame; }
        public void Dispose() { }
    }

    private sealed class FakeEncoder : IFrameEncoder
    {
        public volatile int Frames;
        public volatile bool Finished;
        public volatile bool Disposed;
        public int Width => 16;
        public int Height => 16;
        public void WriteFrame(byte[] bgra) => Frames++;
        public void Finish() => Finished = true;
        public void Dispose() => Disposed = true;
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"shrike-rec-{Guid.NewGuid():N}.mp4");

    [Fact]
    public void Records_then_stops_and_finalises()
    {
        var src = new FakeSource(16, 16);
        var enc = new FakeEncoder();
        var rec = new Recorder(src, enc, TempPath(), fps: 30);

        rec.Start();
        Assert.Equal(RecordingState.Recording, rec.State);
        Thread.Sleep(250);
        var path = rec.Stop();

        Assert.Equal(RecordingState.Stopped, rec.State);
        Assert.True(enc.Finished, "encoder was not finalised");
        Assert.True(enc.Frames > 0, "no frames were written");
        Assert.False(enc.Disposed); // stop finalises, doesn't discard
        rec.Dispose();
        Assert.EndsWith(".mp4", path);
    }

    [Fact]
    public void Pause_halts_frame_flow_then_resume_continues()
    {
        var src = new FakeSource(16, 16);
        var enc = new FakeEncoder();
        using var rec = new Recorder(src, enc, TempPath(), fps: 30);

        rec.Start();
        Thread.Sleep(120);
        rec.Pause();
        Assert.Equal(RecordingState.Paused, rec.State);
        var atPause = enc.Frames;

        Thread.Sleep(200);
        // At most one in-flight frame may land as pause takes effect.
        Assert.True(enc.Frames - atPause <= 1, $"frames grew while paused: {enc.Frames - atPause}");

        rec.Resume();
        Thread.Sleep(120);
        rec.Stop();
        Assert.True(enc.Frames > atPause, "no frames after resume");
    }

    [Fact]
    public void Discard_disposes_and_deletes_the_partial_file()
    {
        var src = new FakeSource(16, 16);
        var enc = new FakeEncoder();
        var path = TempPath();
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 }); // stand-in for ffmpeg's partial output
        var rec = new Recorder(src, enc, path, fps: 30);

        rec.Start();
        Thread.Sleep(80);
        rec.Discard();

        Assert.Equal(RecordingState.Stopped, rec.State);
        Assert.True(enc.Disposed, "encoder was not disposed");
        Assert.False(enc.Finished, "discard must not finalise");
        Assert.False(File.Exists(path), "partial file was not deleted");
    }
}
