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
}

file static class EffectTrackTestExtensions
{
    // Small readable helper for the parity test — append an event to a track.
    public static EffectTrack Append(this EffectTrack track, EffectEvent e)
        => new(track.Events.Append(e));
}
