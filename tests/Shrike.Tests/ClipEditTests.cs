using Shrike.Core.Recording;

namespace Shrike.Tests;

public class ClipEditTests
{
    [Fact]
    public void Round_trips_zoom_events_through_json()
    {
        var edit = new ClipEdit(new ZoomTrack(
        [
            new ZoomEvent(0, 1200, 0.6, 0.4, 1.8, 300, 300),
            new ZoomEvent(2000, 3000, 0.2, 0.8, 2.4, 200, 400),
        ]));

        var back = ClipEdit.FromJson(edit.ToJson());

        Assert.Equal(2, back.Zoom.Events.Count);
        var e = back.Zoom.Events[0];
        Assert.Equal(0, e.StartMs);
        Assert.Equal(1200, e.EndMs);
        Assert.Equal(0.6, e.CenterX, precision: 6);
        Assert.Equal(0.4, e.CenterY, precision: 6);
        Assert.Equal(1.8, e.Zoom, precision: 6);
        Assert.Equal(300, e.EaseInMs);
        Assert.Equal(300, e.EaseOutMs);
    }

    [Fact]
    public void Empty_edit_reports_empty()
    {
        Assert.True(ClipEdit.Empty.IsEmpty);
        Assert.True(new ClipEdit(ZoomTrack.Empty).IsEmpty);
        Assert.True(new ClipEdit(ZoomTrack.Empty, showCursor: true).IsEmpty);
        Assert.False(new ClipEdit(new ZoomTrack([new ZoomEvent(0, 1000, 0.5, 0.5, 2, 100, 100)])).IsEmpty);
        // A non-default "hide cursor" is state worth persisting, so it's not empty.
        Assert.False(new ClipEdit(ZoomTrack.Empty, showCursor: false).IsEmpty);
    }

    [Fact]
    public void Round_trips_show_cursor_default()
    {
        Assert.True(ClipEdit.FromJson(new ClipEdit(ZoomTrack.Empty, showCursor: true).ToJson()).ShowCursor);
        Assert.False(ClipEdit.FromJson(new ClipEdit(ZoomTrack.Empty, showCursor: false).ToJson()).ShowCursor);
        // Older files without the field default to showing the cursor.
        Assert.True(ClipEdit.FromJson("{}").ShowCursor);
    }

    [Fact]
    public void Missing_or_corrupt_file_loads_as_empty()
    {
        Assert.True(ClipEdit.FromJson("{}").IsEmpty); // valid JSON, nothing authored
        Assert.True(ClipEdit.Load(Path.Combine(Path.GetTempPath(), "shrike-nonexistent-" + Guid.NewGuid().ToString("N") + ".edit.json")).IsEmpty);

        // A corrupt file never stops a clip from opening — Load swallows it and returns Empty.
        var bad = Path.Combine(Path.GetTempPath(), "shrike-corrupt-" + Guid.NewGuid().ToString("N") + ".edit.json");
        try { File.WriteAllText(bad, "not json at all"); Assert.True(ClipEdit.Load(bad).IsEmpty); }
        finally { if (File.Exists(bad)) File.Delete(bad); }
    }

    [Fact]
    public void Invalid_events_are_dropped_on_parse()
    {
        // End<=Start and Zoom<=1 are nonsense; parse should filter them.
        var json = ClipEdit.FromJson(edit_json_with_bad_events());
        Assert.Empty(json.Zoom.Events);

        static string edit_json_with_bad_events() => new ClipEdit(new ZoomTrack(
        [
            // Constructed directly (the model allows it); the JSON parser is what filters.
            new ZoomEvent(500, 500, 0.5, 0.5, 2.0, 0, 0), // zero-length
            new ZoomEvent(0, 1000, 0.5, 0.5, 1.0, 0, 0),  // no zoom
        ])).ToJson();
    }

    [Fact]
    public void Save_writes_a_file_and_load_reads_it_back()
    {
        var path = Path.Combine(Path.GetTempPath(), "shrike-edit-" + Guid.NewGuid().ToString("N") + ".edit.json");
        try
        {
            new ClipEdit(new ZoomTrack([new ZoomEvent(0, 1000, 0.5, 0.5, 2.0, 100, 100)])).Save(path);
            Assert.True(File.Exists(path));
            Assert.Single(ClipEdit.Load(path).Zoom.Events);

            // Saving an empty edit removes the file (no stray sidecar for an un-edited clip).
            ClipEdit.Empty.Save(path);
            Assert.False(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // --- Forward migration to the unified effects model (M0) ----------------------------------------------

    [Fact]
    public void ToEffectTrack_carries_zoom_events_unchanged()
    {
        var edit = new ClipEdit(new ZoomTrack([new ZoomEvent(0, 1200, 0.6, 0.4, 1.8, 300, 300)]));
        var effects = edit.ToEffectTrack(clipDurationMs: 5000);

        var zoom = Assert.Single(effects.OfKind<ZoomEffect>());
        Assert.Equal(new ZoomEvent(0, 1200, 0.6, 0.4, 1.8, 300, 300), zoom.ToZoomEvent());
    }

    [Fact]
    public void ToEffectTrack_seeds_a_full_length_visibility_effect_from_show_cursor()
    {
        // Default (cursor shown) → a full-length Visible=true block spanning the whole clip.
        var shown = new ClipEdit(ZoomTrack.Empty, showCursor: true).ToEffectTrack(clipDurationMs: 8000);
        var vShown = Assert.Single(shown.OfKind<VisibilityEffect>());
        Assert.Equal(0, vShown.StartMs);
        Assert.Equal(8000, vShown.EndMs);
        Assert.True(vShown.Visible);

        // Hidden clip → the same full-length block, Visible=false.
        var hidden = new ClipEdit(ZoomTrack.Empty, showCursor: false).ToEffectTrack(clipDurationMs: 8000);
        Assert.False(Assert.Single(hidden.OfKind<VisibilityEffect>()).Visible);
    }

    [Fact]
    public void ToEffectTrack_omits_the_visibility_seed_when_duration_is_unknown()
    {
        // No positive duration → nothing to span, so no seed (rather than a zero-length block).
        Assert.Empty(new ClipEdit(ZoomTrack.Empty, showCursor: true).ToEffectTrack(clipDurationMs: 0)
            .OfKind<VisibilityEffect>());
    }

    [Fact]
    public void ToEffectTrack_migrates_a_loaded_v1_document()
    {
        // The whole point: an existing on-disk (v1) edit projects onto the effects model.
        var v1Json = new ClipEdit(new ZoomTrack([new ZoomEvent(500, 1500, 0.5, 0.5, 2.0, 200, 200)]),
            showCursor: false).ToJson();
        var effects = ClipEdit.FromJson(v1Json).ToEffectTrack(clipDurationMs: 3000);

        Assert.Single(effects.OfKind<ZoomEffect>());
        Assert.False(Assert.Single(effects.OfKind<VisibilityEffect>()).Visible);
    }
}
