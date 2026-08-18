using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shrike.Core.Recording;

/// <summary>
/// The user's authored, non-destructive edit state for one clip — the per-clip "edit document" written next
/// to the recording as <c>*.edit.json</c> (see <see cref="AppStorage.EditDocFor"/>). Today it carries the
/// authored <see cref="ZoomTrack"/>; it's the home for future timeline lanes (callouts, blur, …) so the
/// platform grows by adding sections here rather than new sidecars. Serialisation is forgiving — a missing or
/// malformed file loads as an empty edit — so a clip always opens. UI-free; lives in Core with tests.
/// </summary>
public sealed class ClipEdit
{
    private const int SchemaVersion = 1;

    /// <summary>The authored zoom events. Empty means "no authored zoom".</summary>
    public ZoomTrack Zoom { get; }

    /// <summary>Whether the synthetic cursor is drawn in the preview/export. The clip's default comes from the
    /// capture-time "Show cursor" toggle; the editor can flip it. Default true.</summary>
    public bool ShowCursor { get; }

    public ClipEdit(ZoomTrack? zoom = null, bool showCursor = true)
    {
        Zoom = zoom ?? ZoomTrack.Empty;
        ShowCursor = showCursor;
    }

    public static ClipEdit Empty { get; } = new();

    /// <summary>Nothing to persist — no authored zoom and the cursor is shown (the default). An empty edit
    /// leaves no sidecar on disk.</summary>
    public bool IsEmpty => Zoom.IsEmpty && ShowCursor;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson()
    {
        var dto = new Dto
        {
            V = SchemaVersion,
            ShowCursor = ShowCursor,
            Zoom = Zoom.Events.Select(e => new ZoomDto
            {
                Start = e.StartMs, End = e.EndMs, Cx = e.CenterX, Cy = e.CenterY,
                Zoom = e.Zoom, EaseIn = e.EaseInMs, EaseOut = e.EaseOutMs,
            }).ToArray(),
        };
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    /// <summary>Parse an edit document; tolerant of nulls/partial data (returns whatever it can).</summary>
    public static ClipEdit FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<Dto>(json, JsonOptions);
        if (dto is null) return Empty;
        var events = (dto.Zoom ?? [])
            .Where(z => z.End > z.Start && z.Zoom > 1)
            .Select(z => new ZoomEvent(z.Start, z.End, z.Cx, z.Cy, z.Zoom, z.EaseIn, z.EaseOut))
            .ToList();
        return new ClipEdit(new ZoomTrack(events), dto.ShowCursor);
    }

    /// <summary>Write the edit document to <paramref name="path"/>; deletes it when empty so a clip with no
    /// authored edits leaves no stray file.</summary>
    public void Save(string path)
    {
        if (IsEmpty) { if (File.Exists(path)) File.Delete(path); return; }
        File.WriteAllText(path, ToJson());
    }

    /// <summary>Read an edit document, or <see cref="Empty"/> if it's absent/unreadable/corrupt.</summary>
    public static ClipEdit Load(string path)
    {
        try { return File.Exists(path) ? FromJson(File.ReadAllText(path)) : Empty; }
        catch { return Empty; }
    }

    /// <summary>
    /// Project this (v1-shaped) edit onto the unified <see cref="EffectTrack"/> the effects timeline consumes —
    /// the forward migration. Zoom events become <see cref="ZoomEffect"/>s unchanged; the clip-wide
    /// <see cref="ShowCursor"/> becomes a single <b>full-length</b> <see cref="VisibilityEffect"/> (Visible =
    /// ShowCursor) so the default shows up as an editable block on the lane. <paramref name="clipDurationMs"/> is
    /// the clip's source length, used as the full-length seed's end; a non-positive duration omits the seed
    /// (nothing to span). Deterministic and UI-free — the single home of the v1→effects mapping, unit-tested.
    /// </summary>
    public EffectTrack ToEffectTrack(long clipDurationMs)
    {
        var events = new List<EffectEvent>();
        foreach (var z in Zoom.Events) events.Add(ZoomEffect.FromZoomEvent(z));
        if (clipDurationMs > 0)
            events.Add(new VisibilityEffect(0, clipDurationMs, ShowCursor));
        return new EffectTrack(events);
    }

    private sealed class Dto
    {
        public int V { get; set; } = SchemaVersion;
        public bool ShowCursor { get; set; } = true; // absent in older files → cursor shown
        public ZoomDto[]? Zoom { get; set; }
    }

    private sealed class ZoomDto
    {
        public long Start { get; set; }
        public long End { get; set; }
        public double Cx { get; set; }
        public double Cy { get; set; }
        public double Zoom { get; set; }
        public long EaseIn { get; set; }
        public long EaseOut { get; set; }
    }
}
