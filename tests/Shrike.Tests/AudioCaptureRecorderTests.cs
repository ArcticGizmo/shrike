using System.Diagnostics;
using Shrike.Core.Audio;

namespace Shrike.Tests;

public class AudioCaptureRecorderTests
{
    // A hand-driven audio source: the test raises DataAvailable to simulate the capture thread. Buffers are
    // stamped as captured just now unless the test says how long ago they were captured.
    private sealed class FakeAudioSource : IAudioSource
    {
        public AudioFormat Format { get; init; } = AudioFormat.Default;
        public bool Started;
        public bool Disposed;
        public event Action<byte[], long>? DataAvailable;
        public void Start() => Started = true;
        public void Stop() => Started = false;
        public void Emit(byte[] pcm, long capturedMsAgo = 0) =>
            DataAvailable?.Invoke(pcm, Stopwatch.GetTimestamp() - capturedMsAgo * Stopwatch.Frequency / 1000);
        public void Dispose() => Disposed = true;
    }

    // 1ms at 48k stereo 16-bit.
    private const int OneMs = 192;

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
        clock.Now = 2;           // resumed — the pause-excluded clock picks up where it left off
        src.Emit(Buffer(200));   // recording

        Assert.Equal(600, rec.BytesWritten);
        Assert.Equal(2, rec.DroppedBuffers);
        Assert.Equal(0, rec.SilencePaddedMs);
    }

    [Fact]
    public void Pads_silence_where_the_source_delivered_nothing()
    {
        // Loopback sends no packets while nothing is playing; the gap must stay on the timeline as silence,
        // not collapse and pull later audio early.
        var src = new FakeAudioSource();
        using var ms = new MemoryStream();
        var clock = new Clock { Now = 0 };
        var rec = new AudioCaptureRecorder(src, new WavWriter(ms, src.Format), () => clock.Now);
        rec.Start();

        src.Emit(Buffer(10 * OneMs)); // 0–10ms
        clock.Now = 1000;
        src.Emit(Buffer(10 * OneMs)); // captured at 1000ms, after 990ms of nothing

        var writtenMs = src.Format.BytesToMs(rec.BytesWritten);
        Assert.InRange(writtenMs, 1009, 1011);
        Assert.InRange(rec.SilencePaddedMs, 989, 991);
    }

    [Fact]
    public void Pads_leading_silence_when_the_device_starts_late()
    {
        var src = new FakeAudioSource();
        using var ms = new MemoryStream();
        var clock = new Clock { Now = 300 }; // device opened 300ms into the recording
        var rec = new AudioCaptureRecorder(src, new WavWriter(ms, src.Format), () => clock.Now);
        rec.Start();

        src.Emit(Buffer(10 * OneMs));

        Assert.InRange(src.Format.BytesToMs(rec.BytesWritten), 309, 311);
    }

    [Fact]
    public void Places_a_late_delivered_buffer_by_its_capture_time()
    {
        // A buffer delivered 500ms after capture belongs at its capture time, not its arrival time.
        var src = new FakeAudioSource();
        using var ms = new MemoryStream();
        var clock = new Clock { Now = 1000 };
        var rec = new AudioCaptureRecorder(src, new WavWriter(ms, src.Format), () => clock.Now);
        rec.Start();

        src.Emit(Buffer(10 * OneMs), capturedMsAgo: 500);

        Assert.InRange(src.Format.BytesToMs(rec.BytesWritten), 509, 511);
    }

    [Fact]
    public void Leaves_small_timing_jitter_unpadded()
    {
        var src = new FakeAudioSource();
        using var ms = new MemoryStream();
        var clock = new Clock { Now = 0 };
        var rec = new AudioCaptureRecorder(src, new WavWriter(ms, src.Format), () => clock.Now);
        rec.Start();

        src.Emit(Buffer(10 * OneMs));
        clock.Now = 20; // 10ms past where the stream is — jitter, not a hole
        src.Emit(Buffer(10 * OneMs));

        Assert.Equal(20 * OneMs, rec.BytesWritten);
        Assert.Equal(0, rec.SilencePaddedMs);
    }

    [Fact]
    public void A_constant_clock_never_pads()
    {
        // Voiceover/punch-in drive the recorder with a constant 0 — every buffer is simply appended.
        var src = new FakeAudioSource();
        using var ms = new MemoryStream();
        var rec = new AudioCaptureRecorder(src, new WavWriter(ms, src.Format), () => 0L);
        rec.Start();

        src.Emit(Buffer(10 * OneMs));
        src.Emit(Buffer(10 * OneMs), capturedMsAgo: 200);

        Assert.Equal(20 * OneMs, rec.BytesWritten);
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
