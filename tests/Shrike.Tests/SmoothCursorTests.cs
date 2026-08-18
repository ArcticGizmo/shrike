using Shrike.Core.Capture;
using Shrike.Core.Recording;

namespace Shrike.Tests;

public class SmoothCursorTests
{
    // ---- coordinate mapping ----

    [Fact]
    public void Mapping_handles_region_offset()
    {
        var region = new PixelBounds(100, 50, 640, 360);
        Assert.Equal((0.0, 0.0), CursorMapping.ToExport(100, 50, region, 640, 360));
        Assert.Equal((320.0, 180.0), CursorMapping.ToExport(420, 230, region, 640, 360));
    }

    [Fact]
    public void Mapping_is_identity_at_native_size()
    {
        var region = new PixelBounds(0, 0, 640, 360); // e.g. an odd width already trimmed to even
        Assert.Equal((640.0, 360.0), CursorMapping.ToExport(640, 360, region, 640, 360));
    }

    [Fact]
    public void Mapping_applies_a_downscale()
    {
        var region = new PixelBounds(0, 0, 640, 360);
        Assert.Equal((320.0, 180.0), CursorMapping.ToExport(640, 360, region, 320, 180));
        Assert.Equal((160.0, 90.0), CursorMapping.ToExport(320, 180, region, 320, 180));
    }

    [Fact]
    public void Mapping_handles_a_second_monitor_origin()
    {
        var region = new PixelBounds(1920, 0, 800, 600); // a monitor at a non-zero virtual origin
        Assert.Equal((0.0, 0.0), CursorMapping.ToExport(1920, 0, region, 800, 600));
        Assert.Equal((800.0, 600.0), CursorMapping.ToExport(2720, 600, region, 800, 600));
    }

    // ---- projection ----

    // A track where x == time in ms (0..durationMs) and y == 0, one point every `stepMs`.
    private static MouseTrack LinearTrack(int durationMs, int stepMs, PixelBounds region, IReadOnlyList<MouseClick>? clicks = null)
    {
        var pts = new List<MousePoint>();
        for (var t = 0; t <= durationMs; t += stepMs) pts.Add(new MousePoint(t, t, 0));
        return new MouseTrack(region, pts, clicks ?? []);
    }

    // Near-identity smoothing so a projected position tracks its source position (lets us assert geometry).
    private static readonly CursorSmoothing PassThrough = new(MinCutoff: 1_000_000, Beta: 0.0);

    [Fact]
    public void Projects_one_frame_per_output_frame()
    {
        var region = new PixelBounds(0, 0, 1000, 100);
        var track = LinearTrack(1000, 20, region);
        var result = SmoothCursor.Project(track, new Timeline(1000), fps: 10, 1000, 100, PassThrough);
        Assert.Equal(10, result.Frames.Count); // 1000 ms * 10 fps / 1000
    }

    [Fact]
    public void Empty_track_yields_no_frames()
    {
        var region = new PixelBounds(0, 0, 100, 100);
        var track = new MouseTrack(region, [], []);
        var result = SmoothCursor.Project(track, new Timeline(1000), fps: 30, 100, 100);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Cursor_jumps_across_a_cut_rather_than_gliding()
    {
        var region = new PixelBounds(0, 0, 1000, 100);
        var track = LinearTrack(1000, 20, region);
        var timeline = new Timeline(1000);
        timeline.Cut(300, 600); // kept: [0,300) + [600,1000) → 700 ms edited

        var result = SmoothCursor.Project(track, timeline, fps: 10, 1000, 100, PassThrough);

        Assert.Equal(7, result.Frames.Count); // 700 ms * 10 fps / 1000
        // Frames 0..2 map to source 0,100,200; frame 3 lands at source 600 (the cut boundary).
        Assert.Equal(200.0, result.Frames[2].X, precision: 0);
        Assert.Equal(600.0, result.Frames[3].X, precision: 0);
        // i.e. a ~400px jump, not the ~100px step of un-cut motion.
        Assert.True(result.Frames[3].X - result.Frames[2].X > 300);
    }

    [Fact]
    public void Position_clamps_before_the_first_and_after_the_last_point()
    {
        // Points only cover 200..800 ms; frames outside that hold the nearest end.
        var region = new PixelBounds(0, 0, 1000, 100);
        var pts = new List<MousePoint>();
        for (var t = 200; t <= 800; t += 20) pts.Add(new MousePoint(t, t, 0));
        var track = new MouseTrack(region, pts, []);

        var result = SmoothCursor.Project(track, new Timeline(1000), fps: 10, 1000, 100, PassThrough);

        Assert.Equal(200.0, result.Frames[0].X, precision: 0);   // before first point → clamp to 200
        Assert.Equal(800.0, result.Frames[^1].X, precision: 0);  // after last point → clamp to 800
    }

    [Fact]
    public void Clicks_map_to_frames_and_drop_inside_a_cut()
    {
        var region = new PixelBounds(0, 0, 1000, 100);
        var clicks = new[]
        {
            new MouseClick(100, MouseButtonKind.Left, true),   // kept → frame 1 at 10 fps
            new MouseClick(450, MouseButtonKind.Left, true),   // inside the cut → dropped
            new MouseClick(150, MouseButtonKind.Left, false),  // button-up → ignored
        };
        var track = LinearTrack(1000, 20, region, clicks);
        var timeline = new Timeline(1000);
        timeline.Cut(300, 600);

        var result = SmoothCursor.Project(track, timeline, fps: 10, 1000, 100, PassThrough);

        Assert.Single(result.Clicks);
        Assert.Equal(1, result.Clicks[0].FrameIndex);
        Assert.Equal(MouseButtonKind.Left, result.Clicks[0].Button);
    }
}
