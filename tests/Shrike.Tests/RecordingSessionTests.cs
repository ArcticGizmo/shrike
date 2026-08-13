using Shrike.Core.Recording;

namespace Shrike.Tests;

public class RecordingSessionTests
{
    // Records how the session drives the encoder, without touching ffmpeg.
    private sealed class FakeEncoder : IFrameEncoder
    {
        public int Frames;
        public bool Finished;
        public bool Disposed;
        public int Width => 4;
        public int Height => 4;
        public void WriteFrame(byte[] bgra) => Frames++;
        public void Finish() => Finished = true;
        public void Dispose() => Disposed = true;
    }

    private static readonly byte[] AnyFrame = new byte[4 * 4 * 4];

    [Fact]
    public void Emits_roughly_fps_frames_per_second()
    {
        var enc = new FakeEncoder();
        var s = new RecordingSession(enc, fps: 30);
        s.Start(0);

        // Tick every 10ms for 1000ms — far faster than 30fps, so the session should throttle to ~30.
        for (long t = 0; t <= 1000; t += 10)
            s.Tick(t, AnyFrame);

        // Frames due by 1000ms at 30fps: indices 0..30 => 31 frames.
        Assert.InRange(enc.Frames, 30, 31);
    }

    [Fact]
    public void Duplicates_the_last_frame_when_ticks_are_sparse()
    {
        var enc = new FakeEncoder();
        var s = new RecordingSession(enc, fps: 30);
        s.Start(0);

        // A single late tick at 1000ms must fill the whole second by duplicating.
        var written = s.Tick(1000, AnyFrame);
        Assert.InRange(written, 30, 31);
        Assert.InRange(enc.Frames, 30, 31);
    }

    [Fact]
    public void Pause_excludes_time_from_the_timeline()
    {
        var enc = new FakeEncoder();
        var s = new RecordingSession(enc, fps: 30);
        s.Start(0);
        s.Tick(500, AnyFrame);          // ~15 frames by 500ms
        var atPause = enc.Frames;

        s.Pause(500);
        s.Tick(5000, AnyFrame);         // ticks while paused do nothing
        Assert.Equal(atPause, enc.Frames);
        Assert.Equal(500, s.ElapsedMs(5000)); // elapsed frozen during pause

        s.Resume(5000);
        s.Tick(5500, AnyFrame);         // 500ms more active time => ~15 more frames
        Assert.Equal(1000, s.ElapsedMs(5500));
        Assert.InRange(enc.Frames, 30, 31);
    }

    [Fact]
    public void Ticks_before_start_and_after_stop_do_nothing()
    {
        var enc = new FakeEncoder();
        var s = new RecordingSession(enc, fps: 30);

        Assert.Equal(0, s.Tick(100, AnyFrame)); // Idle
        s.Start(0);
        s.Tick(1000, AnyFrame);
        var atStop = enc.Frames;
        s.Stop();
        Assert.True(enc.Finished);

        Assert.Equal(0, s.Tick(2000, AnyFrame)); // Stopped
        Assert.Equal(atStop, enc.Frames);
    }

    [Fact]
    public void Discard_disposes_without_finalising()
    {
        var enc = new FakeEncoder();
        var s = new RecordingSession(enc, fps: 30);
        s.Start(0);
        s.Tick(500, AnyFrame);
        s.Discard();

        Assert.True(enc.Disposed);
        Assert.False(enc.Finished);
        Assert.Equal(RecordingState.Stopped, s.State);
    }

    [Fact]
    public void Illegal_transitions_throw()
    {
        var enc = new FakeEncoder();
        var s = new RecordingSession(enc, fps: 30);

        Assert.Throws<InvalidOperationException>(() => s.Pause(0));   // not recording
        Assert.Throws<InvalidOperationException>(() => s.Resume(0));  // not paused
        s.Start(0);
        Assert.Throws<InvalidOperationException>(() => s.Start(0));   // already started
    }
}
