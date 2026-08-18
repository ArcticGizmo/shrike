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
        Assert.False(new ClipEdit(new ZoomTrack([new ZoomEvent(0, 1000, 0.5, 0.5, 2, 100, 100)])).IsEmpty);
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
}
