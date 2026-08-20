using Shrike.Core.Recording;

namespace Shrike.Tests;

public class CaptionEffectTests
{
    [Fact]
    public void FromCues_orders_cues_and_spans_them()
    {
        var effect = CaptionEffect.FromCues(
        [
            new CaptionCue(2000, 2500, "second"),
            new CaptionCue(500, 1000, "first"),
        ]);

        Assert.Equal(EffectKind.Caption, effect.Kind);
        Assert.Equal("first", effect.Cues[0].Text);   // reordered by start
        Assert.Equal("second", effect.Cues[1].Text);
        Assert.Equal(500, effect.StartMs);            // spans min start …
        Assert.Equal(2500, effect.EndMs);             // … to max end
    }

    [Fact]
    public void FromCues_with_no_cues_is_a_zero_length_block()
    {
        var effect = CaptionEffect.FromCues([]);
        Assert.Empty(effect.Cues);
        Assert.Equal(0, effect.StartMs);
        Assert.Equal(0, effect.EndMs);
    }

    [Fact]
    public void Cue_span_is_half_open()
    {
        var cue = new CaptionCue(1000, 2000, "hi");
        Assert.True(cue.ActiveAt(1000));   // start included
        Assert.False(cue.ActiveAt(2000));  // end excluded
        Assert.Equal(1000, cue.DurationMs);
    }

    [Fact]
    public void CaptionAt_picks_the_active_cue_and_is_inactive_outside()
    {
        var effect = CaptionEffect.FromCues(
        [
            new CaptionCue(0, 1000, "one"),
            new CaptionCue(1000, 2000, "two"),
        ], CaptionStyle.Default with { FadeMs = 0 }); // no fade → alpha is a clean 1/0

        Assert.Equal(0, EffectTrack.CaptionAt(effect, 500).CueIndex);
        Assert.Equal(1, EffectTrack.CaptionAt(effect, 1500).CueIndex);
        Assert.Equal(1.0, EffectTrack.CaptionAt(effect, 1500).Alpha, 6);

        Assert.False(EffectTrack.CaptionAt(effect, 2500).Active); // past the last cue
        Assert.Equal(-1, CaptionFrame.Inactive.CueIndex);
    }

    [Fact]
    public void CaptionAt_last_active_cue_wins_on_overlap()
    {
        var effect = CaptionEffect.FromCues(
        [
            new CaptionCue(0, 2000, "under"),
            new CaptionCue(1000, 3000, "over"),
        ], CaptionStyle.Default with { FadeMs = 0 });

        // At 1500ms both cover it; the later-ordered cue wins (matches lane draw order).
        Assert.Equal("over", effect.Cues[EffectTrack.CaptionAt(effect, 1500).CueIndex].Text);
    }

    [Fact]
    public void CaptionAt_crossfades_at_the_cue_edges()
    {
        var effect = CaptionEffect.FromCues(
            [new CaptionCue(1000, 3000, "fade me")],
            CaptionStyle.Default with { FadeMs = 100 });

        // Smoothstep(0.5) = 0.5 exactly, at half-way into the fade.
        Assert.Equal(0.5, EffectTrack.CaptionAt(effect, 1050).Alpha, 6); // 50ms into a 100ms fade-in
        Assert.Equal(0.5, EffectTrack.CaptionAt(effect, 2950).Alpha, 6); // 50ms before the end (fade-out)
        Assert.Equal(1.0, EffectTrack.CaptionAt(effect, 2000).Alpha, 6); // mid-hold, fully up
    }

    [Fact]
    public void CaptionAt_clamps_fade_to_half_duration_for_short_cues()
    {
        // A 100ms cue with a 200ms fade: the fade clamps to 50ms each side, peaking at the midpoint and never
        // overshooting — so a very short line still shows cleanly.
        var effect = CaptionEffect.FromCues([new CaptionCue(0, 100, "quick")], CaptionStyle.Default with { FadeMs = 200 });
        Assert.Equal(1.0, EffectTrack.CaptionAt(effect, 50).Alpha, 3);        // Smoothstep(50/50) = 1 at the middle
        Assert.InRange(EffectTrack.CaptionAt(effect, 10).Alpha, 0.0, 1.0);    // ramping, never > 1
        Assert.True(EffectTrack.CaptionAt(effect, 10).Alpha < 1.0);          // still inside the fade-in
    }

    [Fact]
    public void HasCaptions_only_when_a_caption_effect_carries_cues()
    {
        Assert.False(new EffectTrack([]).HasCaptions);
        Assert.False(new EffectTrack([CaptionEffect.FromCues([])]).HasCaptions);   // placed, not transcribed
        Assert.True(new EffectTrack([CaptionEffect.FromCues([new CaptionCue(0, 500, "x")])]).HasCaptions);
    }

    // --- per-output-frame resolver: cues are source-time, so they ride cuts -------------------------------

    [Fact]
    public void ResolveCaptions_maps_frames_to_source_time()
    {
        var timeline = new Timeline(2000); // no cuts → edited time == source time
        var effect = CaptionEffect.FromCues(
            [new CaptionCue(500, 1500, "hello")],
            CaptionStyle.Default with { FadeMs = 0 });
        var frames = EffectTrack.ResolveCaptions(effect, timeline, frameCount: 40, fps: 20); // 50ms/frame

        Assert.False(frames[0].Active);   // 0ms   — before the cue
        Assert.True(frames[20].Active);   // 1000ms — inside
        Assert.Equal(0, frames[20].CueIndex);
        Assert.False(frames[34].Active);  // 1700ms — after
    }

    [Fact]
    public void ResolveCaptions_rides_a_mid_clip_cut()
    {
        // Cut out [1000,2000) of a 4s source. A cue anchored at source [2500,3000) must still show — now at
        // edited time ~1500ms, because it's linked to source time. A cue buried inside the cut never shows.
        var timeline = new Timeline(4000);
        timeline.Cut(1000, 2000); // kept ranges: [0,1000) + [2000,4000); edited length 3000ms
        var effect = CaptionEffect.FromCues(
        [
            new CaptionCue(2500, 3000, "survives"),
            new CaptionCue(1200, 1800, "buried in the cut"),
        ], CaptionStyle.Default with { FadeMs = 0 });

        var frames = EffectTrack.ResolveCaptions(effect, timeline, frameCount: 60, fps: 20); // edited 0..3000ms

        // Edited 1500ms (frame 30) → source 2500ms → the surviving cue.
        Assert.True(frames[30].Active);
        Assert.Equal("survives", effect.Cues[frames[30].CueIndex].Text);

        // No edited frame ever maps into the cut span, so the buried cue is never drawn.
        Assert.DoesNotContain(frames, f => f.Active && effect.Cues[f.CueIndex].Text == "buried in the cut");
    }
}
