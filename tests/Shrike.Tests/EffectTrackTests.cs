using Shrike.Core.Recording;

namespace Shrike.Tests;

public class EffectTrackTests
{
    [Fact]
    public void Orders_events_by_start_then_end()
    {
        var track = new EffectTrack(
        [
            new RippleEffect(2000, 2500),
            new VisibilityEffect(0, 5000, true),
            new ZoomEffect(2000, 3000, 200, 200, 0.5, 0.5, 1.8), // same start as the ripple, later end
        ]);

        Assert.Equal([0L, 2000L, 2000L], track.Events.Select(e => e.StartMs).ToArray());
        // The two events starting at 2000 are ordered by end (ripple 2500 before zoom 3000).
        Assert.IsType<RippleEffect>(track.Events[1]);
        Assert.IsType<ZoomEffect>(track.Events[2]);
    }

    [Fact]
    public void OfKind_returns_only_that_kind()
    {
        var track = new EffectTrack(
        [
            new ZoomEffect(0, 1000, 100, 100, 0.5, 0.5, 2.0),
            new ZoomEffect(2000, 3000, 100, 100, 0.5, 0.5, 2.0),
            new VisibilityEffect(0, 3000, false),
            new SpotlightEffect(500, 900, 100, 100, "#FFD24A", 0.3, 30),
        ]);

        Assert.Equal(2, track.OfKind<ZoomEffect>().Count());
        Assert.Single(track.OfKind<VisibilityEffect>());
        Assert.Single(track.OfKind<SpotlightEffect>());
        Assert.Empty(track.OfKind<RippleEffect>());
    }

    [Fact]
    public void Empty_track_is_empty()
    {
        Assert.True(EffectTrack.Empty.IsEmpty);
        Assert.False(new EffectTrack([new RippleEffect(0, 100)]).IsEmpty);
    }

    // --- The envelope matches ZoomEvent's, so effects fade identically ------------------------------------

    [Fact]
    public void Ramp_is_zero_outside_the_span_and_one_across_the_hold()
    {
        var e = new SpotlightEffect(1000, 3000, 400, 400, "#FFD24A", 0.3, 30);
        Assert.Equal(0.0, e.RampAt(1000));           // at the start edge
        Assert.Equal(0.0, e.RampAt(500));            // before
        Assert.Equal(0.0, e.RampAt(3000));           // at the end edge
        Assert.Equal(0.0, e.RampAt(4000));           // after
        Assert.Equal(1.0, e.RampAt(2000), 6);        // mid-hold (past ease-in, before ease-out)
    }

    [Fact]
    public void Span_is_half_open_active_at_the_start_frame_not_the_end()
    {
        // Regression: an effect must be active at exactly its start (the playhead often sits there right after
        // adding), and a hard-cut effect is at full strength there — otherwise it looks like nothing appears.
        var hardCut = new CanvasEffect(1000, 2000, 0, 0, CanvasSpace.Content);
        Assert.True(hardCut.ActiveAt(1000));       // start included
        Assert.False(hardCut.ActiveAt(2000));      // end excluded (belongs to whatever follows)
        Assert.Equal(1.0, hardCut.RampAt(1000), 6); // full strength at the start frame
        Assert.Equal(0.0, hardCut.RampAt(2000), 6);
    }

    [Fact]
    public void Ramp_matches_zoom_events_envelope_exactly()
    {
        // A ZoomEffect and the equivalent ZoomEvent must agree on the eased envelope at every sample.
        var effect = new ZoomEffect(0, 2000, 500, 500, 0.5, 0.5, 2.0);
        var legacy = effect.ToZoomEvent();
        for (long t = -100; t <= 2100; t += 50)
            Assert.Equal(legacy.RampAt(t), effect.RampAt(t), 9);
    }

    // --- Zoom resolves byte-for-byte identically to the standalone ZoomTrack ------------------------------

    [Fact]
    public void ResolveZoom_is_identical_to_the_legacy_zoom_track()
    {
        var timeline = new Timeline(4000);
        var zoomEvents = new List<ZoomEvent>
        {
            new(0, 1200, 0.6, 0.4, 1.8, 300, 300),
            new(2000, 3500, 0.2, 0.8, 2.4, 200, 400),
        };
        var legacy = new ZoomTrack(zoomEvents);
        var unified = new EffectTrack(zoomEvents.Select(ZoomEffect.FromZoomEvent))
            .Append(new VisibilityEffect(0, 4000, true))  // non-zoom effects must not change framing
            .Append(new RippleEffect(500, 900));

        const int fps = 30, w = 1280, h = 720;
        var frameCount = 120;
        var a = legacy.Resolve(timeline, frameCount, fps, w, h);
        var b = unified.ResolveZoom(timeline, frameCount, fps, w, h);

        Assert.Equal(a.Length, b.Length);
        for (var i = 0; i < a.Length; i++)
            Assert.Equal(a[i], b[i]); // ZoomViewport is a record — exact structural equality
    }

    [Fact]
    public void VisibilityAt_and_RipplesEnabledAt_read_the_active_range()
    {
        var track = new EffectTrack(
        [
            new VisibilityEffect(0, 5000, true),
            new VisibilityEffect(2000, 3000, false), // a hide span punched into the default
            new RippleEffect(1000, 1500),
        ]);

        Assert.True(track.VisibilityAt(500)!.Visible);   // default shown
        Assert.False(track.VisibilityAt(2500)!.Visible); // inside the hide span (last one wins)
        Assert.True(track.VisibilityAt(4000)!.Visible);  // back to default

        Assert.True(track.RipplesEnabledAt(1200));
        Assert.False(track.RipplesEnabledAt(1800));
    }

    // --- per-output-frame resolvers -----------------------------------------------------------------------

    [Fact]
    public void ResolveCursorVisible_defaults_true_and_a_hide_span_masks_its_frames()
    {
        var timeline = new Timeline(2000); // no cuts → edited time == source time
        var track = new EffectTrack([new VisibilityEffect(500, 1500, false)]);
        var mask = track.ResolveCursorVisible(timeline, frameCount: 40, fps: 20); // 50ms per frame

        Assert.True(mask[0]);    // 0ms  — default shown
        Assert.False(mask[20]);  // 1000ms — inside the hide span
        Assert.True(mask[39]);   // 1950ms — after it, shown again
    }

    [Fact]
    public void ResolveRipplesEnabled_is_true_only_inside_a_ripple_span()
    {
        var timeline = new Timeline(2000);
        var track = new EffectTrack([new RippleEffect(1000, 1500)]);
        var mask = track.ResolveRipplesEnabled(timeline, frameCount: 40, fps: 20);

        Assert.False(mask[10]); // 500ms
        Assert.True(mask[24]);  // 1200ms
        Assert.False(mask[34]); // 1700ms
    }

    [Fact]
    public void ResolveSpotlight_is_active_and_eased_inside_its_span()
    {
        var timeline = new Timeline(2000);
        var track = new EffectTrack([new SpotlightEffect(0, 2000, 500, 500, "#FF0000", 0.5, 40)]);
        var frames = track.ResolveSpotlight(timeline, frameCount: 40, fps: 20, height: 1080);

        var mid = frames[20]; // 1000ms — mid-hold, fully ramped
        Assert.True(mid.Active);
        Assert.Equal(0.5, mid.Alpha, 3);           // Opacity * ramp(=1)
        Assert.Equal((byte)0xFF, mid.R);           // parsed colour
        Assert.Equal((byte)0x00, mid.G);
        Assert.True(mid.RadiusPx > 0);

        // Outside any spotlight → inactive.
        Assert.False(new EffectTrack([]).ResolveSpotlight(timeline, 40, 20, 1080)[20].Active);
    }

    [Fact]
    public void ParseHex_reads_rgb_and_falls_back_on_junk()
    {
        Assert.Equal(((byte)0xFF, (byte)0xD2, (byte)0x4A), EffectTrack.ParseHex("#FFD24A"));
        Assert.Equal(((byte)0x10, (byte)0x20, (byte)0x30), EffectTrack.ParseHex("102030"));
        Assert.Equal(((byte)0x11, (byte)0x22, (byte)0x33), EffectTrack.ParseHex("#FF112233")); // AARRGGBB → RGB
        Assert.Equal(((byte)0xFF, (byte)0xD2, (byte)0x4A), EffectTrack.ParseHex("not-a-colour"));
    }
}

file static class EffectTrackTestExtensions
{
    // Small readable helper for the parity test — append an event to a track.
    public static EffectTrack Append(this EffectTrack track, EffectEvent e)
        => new(track.Events.Append(e));
}
