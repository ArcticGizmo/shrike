using Shrike.Core.Audio;

namespace Shrike.Tests;

public class AudioCaptureRecorderTests
{
    // A hand-driven audio source: the test raises DataAvailable to simulate the capture thread.
    private sealed class FakeAudioSource : IAudioSource
    {
        public AudioFormat Format { get; init; } = AudioFormat.Default;
        public bool Started;
        public bool Disposed;
        public event Action<byte[]>? DataAvailable;
        public void Start() => Started = true;
        public void Stop() => Started = false;
        public void Emit(byte[] pcm) => DataAvailable?.Invoke(pcm);
        public void Dispose() => Disposed = true;
    }

    // A clock the test moves between "recording" (a value) and "paused/stopped" (null).
    private sealed class Clock { public long? Now; }

    private static byte[] Buffer(int bytes) => new byte[bytes];

    [Fact]
    public void Writes_buffers_while_recording()
    {
        var src = new FakeAudioSource();
        using var ms = new MemoryStream();
        var clock = new Clock { Now = 0 };
        var rec = new AudioCaptureRecorder(src, new WavWriter(ms, src.Format), () => clock.Now);

        rec.Start();
        Assert.True(src.Started);
        src.Emit(Buffer(400));
        src.Emit(Buffer(600));

        Assert.Equal(1000, rec.BytesWritten);
        Assert.Equal(0, rec.DroppedBuffers);
    }

    [Fact]
    public void Drops_buffers_that_arrive_while_paused()
    {
        var src = new FakeAudioSource();
        using var ms = new MemoryStream();
        var clock = new Clock { Now = 0 };
        var rec = new AudioCaptureRecorder(src, new WavWriter(ms, src.Format), () => clock.Now);
        rec.Start();

        src.Emit(Buffer(400));   // recording
        clock.Now = null;        // paused
        src.Emit(Buffer(400));   // dropped
        src.Emit(Buffer(400));   // dropped
        clock.Now = 500;         // resumed
        src.Emit(Buffer(200));   // recording

        Assert.Equal(600, rec.BytesWritten);
        Assert.Equal(2, rec.DroppedBuffers);
    }

    [Fact]
    public void Dispose_finalises_a_readable_sidecar()
    {
        var src = new FakeAudioSource();
        var ms = new MemoryStream();
        var clock = new Clock { Now = 0 };
        var rec = new AudioCaptureRecorder(src, new WavWriter(ms, src.Format), () => clock.Now);
        rec.Start();
        src.Emit(new byte[192]); // 1ms at 48k stereo 16-bit

        rec.Dispose();
        Assert.True(src.Disposed);

        ms.Position = 0;
        var (format, pcm) = WavFile.Read(ms);
        Assert.Equal(AudioFormat.Default, format);
        Assert.Equal(192, pcm.Length);
    }

    [Fact]
    public void Ignores_buffers_after_dispose()
    {
        var src = new FakeAudioSource();
        var ms = new MemoryStream();
        var rec = new AudioCaptureRecorder(src, new WavWriter(ms, src.Format), () => 0L);
        rec.Start();
        rec.Dispose();

        // A late buffer from a lagging capture thread must not throw or write.
        src.Emit(new byte[100]);
        Assert.Equal(0, rec.DroppedBuffers);
    }

    [Fact]
    public void Mismatched_source_and_sidecar_format_throws()
    {
        var src = new FakeAudioSource { Format = new AudioFormat(44_100, 1, 16) };
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentException>(() =>
            new AudioCaptureRecorder(src, new WavWriter(ms, AudioFormat.Default), () => 0L));
    }
}
