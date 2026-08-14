using Shrike.Core.Capture;
using Shrike.Core.Recording;

namespace Shrike.Tests;

public class MouseTrackTests
{
    private static readonly PixelBounds Region = new(100, 50, 640, 360);

    [Fact]
    public void Json_round_trips_region_points_and_clicks()
    {
        var track = new MouseTrack(
            Region,
            [new MousePoint(0, 120, 60), new MousePoint(16, 130, 62), new MousePoint(33, 145, 70)],
            [new MouseClick(20, MouseButtonKind.Left, true), new MouseClick(120, MouseButtonKind.Left, false)]);

        var back = MouseTrack.FromJson(track.ToJson());

        Assert.Equal(Region, back.Region);
        Assert.Equal(track.Points, back.Points);
        Assert.Equal(track.Clicks, back.Clicks);
    }

    [Fact]
    public void Json_round_trips_an_empty_track()
    {
        var track = new MouseTrack(Region, [], []);
        var back = MouseTrack.FromJson(track.ToJson());

        Assert.Equal(Region, back.Region);
        Assert.Empty(back.Points);
        Assert.Empty(back.Clicks);
    }

    [Fact]
    public void FromJson_rejects_a_malformed_region()
    {
        Assert.ThrowsAny<Exception>(() => MouseTrack.FromJson("{\"v\":1,\"region\":[1,2]}"));
    }

    [Fact]
    public void Recorder_stamps_events_with_the_capture_clock()
    {
        long? now = 0;
        var rec = new MouseTrackRecorder(Region, () => now);

        now = 0; rec.Move(10, 10);
        now = 16; rec.Move(11, 12);
        now = 33; rec.Click(MouseButtonKind.Left, true);

        var track = rec.Build();
        Assert.Equal(new[] { 0, 16 }, track.Points.Select(p => p.TMs));
        Assert.Equal(new[] { (11, 12) }, track.Points.Skip(1).Select(p => (p.X, p.Y)));
        Assert.Single(track.Clicks);
        Assert.Equal(33, track.Clicks[0].TMs);
    }

    [Fact]
    public void Recorder_drops_events_while_paused_and_excludes_the_gap()
    {
        // The capture clock returns null while paused (see Recorder.CaptureTimeMs); those events vanish,
        // and after resume the timeline continues without the paused wall-time.
        long? now = 0;
        var rec = new MouseTrackRecorder(Region, () => now);

        now = 0; rec.Move(1, 1);
        now = 40; rec.Move(2, 2);
        now = null; rec.Move(999, 999); rec.Move(999, 999); rec.Click(MouseButtonKind.Right, true); // paused
        now = 80; rec.Move(3, 3);

        var track = rec.Build();
        Assert.Equal(new[] { 0, 40, 80 }, track.Points.Select(p => p.TMs));
        Assert.DoesNotContain(track.Points, p => p is { X: 999, Y: 999 });
        Assert.Empty(track.Clicks); // the only click happened while paused
    }

    [Fact]
    public void Recorder_build_carries_the_region()
    {
        var rec = new MouseTrackRecorder(Region, () => 0L);
        rec.Move(5, 5);
        Assert.Equal(Region, rec.Build().Region);
    }
}
