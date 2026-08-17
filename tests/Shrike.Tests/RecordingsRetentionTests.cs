using Shrike.Core;
using Shrike.Core.Recording;

namespace Shrike.Tests;

public class RecordingsRetentionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1000);

    private static RecordingFile F(string name, DateTimeOffset when, long size = 10) => new(name, size, when);

    // ---- ToEvict (pure) ----

    [Fact]
    public void Empty_evicts_nothing()
    {
        Assert.Empty(RecordingsRetention.ToEvict([], RecordingRetention.Default, Now));
    }

    [Fact]
    public void The_only_recording_is_never_evicted_even_if_old_and_huge()
    {
        var files = new[] { F("solo", Now.AddYears(-1), size: long.MaxValue) };
        var policy = new RecordingRetention(MaxCount: 1, MaxBytes: 1, MaxAge: TimeSpan.FromMinutes(1));
        Assert.Empty(RecordingsRetention.ToEvict(files, policy, Now));
    }

    [Fact]
    public void Count_cap_evicts_the_oldest_beyond_the_limit()
    {
        var files = new[]
        {
            F("f0", Now), F("f1", Now.AddMinutes(-1)), F("f2", Now.AddMinutes(-2)),
            F("f3", Now.AddMinutes(-3)), F("f4", Now.AddMinutes(-4)),
        };
        var policy = new RecordingRetention(MaxCount: 3, MaxBytes: long.MaxValue, MaxAge: TimeSpan.FromDays(3650));
        var evicted = RecordingsRetention.ToEvict(files, policy, Now).Select(e => e.Path);
        Assert.Equal(new[] { "f3", "f4" }, evicted);
    }

    [Fact]
    public void Byte_cap_evicts_oldest_first_and_always_keeps_the_newest()
    {
        // Five 10-byte files, cap 25: newest (10) + next (20) fit; the rest push over and go.
        var files = new[]
        {
            F("f0", Now), F("f1", Now.AddMinutes(-1)), F("f2", Now.AddMinutes(-2)),
            F("f3", Now.AddMinutes(-3)), F("f4", Now.AddMinutes(-4)),
        };
        var policy = new RecordingRetention(MaxCount: 999, MaxBytes: 25, MaxAge: TimeSpan.FromDays(3650));
        var evicted = RecordingsRetention.ToEvict(files, policy, Now).Select(e => e.Path);
        Assert.Equal(new[] { "f2", "f3", "f4" }, evicted);
    }

    [Fact]
    public void Age_evicts_stale_recordings_but_keeps_the_newest()
    {
        var files = new[]
        {
            F("fresh", Now), F("recent", Now.AddDays(-2)),
            F("stale", Now.AddDays(-10)), F("ancient", Now.AddDays(-30)),
        };
        var policy = new RecordingRetention(MaxCount: 999, MaxBytes: long.MaxValue, MaxAge: TimeSpan.FromDays(7));
        var evicted = RecordingsRetention.ToEvict(files, policy, Now).Select(e => e.Path);
        Assert.Equal(new[] { "stale", "ancient" }, evicted);
    }

    // ---- Sweep (real folder) ----

    private static (string mp4, string side) Make(string dir, string name, DateTimeOffset when, int bytes = 16)
    {
        var mp4 = Path.Combine(dir, $"shrike-{name}.mp4");
        var side = AppStorage.SidecarFor(mp4);
        File.WriteAllBytes(mp4, new byte[bytes]);
        File.WriteAllText(side, "{}");
        File.SetLastWriteTimeUtc(mp4, when.UtcDateTime);
        return (mp4, side);
    }

    [Fact]
    public void Sweep_deletes_evicted_recordings_with_their_sidecars_and_keeps_the_rest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shrike-rettest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Make(dir, "a", Now);                 // newest — kept
            var b = Make(dir, "b", Now.AddDays(-2));     // recent — kept
            var c = Make(dir, "c", Now.AddDays(-10));    // stale — evicted with its sidecar
            var policy = new RecordingRetention(MaxCount: 100, MaxBytes: long.MaxValue, MaxAge: TimeSpan.FromDays(7));

            var deleted = RecordingsRetention.Sweep(dir, policy, Now);

            Assert.Equal(1, deleted);
            Assert.True(File.Exists(a.mp4) && File.Exists(a.side));
            Assert.True(File.Exists(b.mp4) && File.Exists(b.side));
            Assert.False(File.Exists(c.mp4));
            Assert.False(File.Exists(c.side)); // the sidecar went with the recording
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Sweep_removes_orphaned_sidecars()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shrike-rettest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var live = Make(dir, "live", Now);                       // mp4 + sidecar
            var orphan = Path.Combine(dir, "shrike-orphan.track.json");
            File.WriteAllText(orphan, "{}");                          // sidecar with no recording

            RecordingsRetention.Sweep(dir, RecordingRetention.Default, Now);

            Assert.True(File.Exists(live.mp4) && File.Exists(live.side));
            Assert.False(File.Exists(orphan));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
