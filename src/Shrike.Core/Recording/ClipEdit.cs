using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shrike.Core.Recording;

/// <summary>
/// The user's authored, non-destructive edit state for one clip — the per-clip "edit document" written next
/// to the recording as <c>*.edit.json</c> (see <see cref="AppStorage.EditDocFor"/>). It stores the unified
/// <see cref="EffectTrack"/> (zoom, spotlight, click-ripple, mouse-visibility; the canvas payload lands with
/// that milestone). Two on-disk shapes are read: the legacy <b>v1</b> (an authored zoom list + a single
/// <c>ShowCursor</c> flag), migrated on load, and the current <b>v2</b> effect track. Serialisation is
/// forgiving — a missing or malformed file loads as an empty edit — so a clip always opens. UI-free; lives in
/// Core with tests.
/// </summary>
public sealed class ClipEdit
{
    private const int SchemaVersion = 2;

    /// <summary>The authored effects. For a v2 document this is the stored track; for a v1 document it is the
    /// zoom events only (visibility is migrated from <see cref="ShowCursor"/> by the editor, which knows the
    /// clip duration needed for the full-length seed — see <see cref="ToEffectTrack"/>).</summary>
    public EffectTrack Effects { get; }

    /// <summary>True when this came from (or was built as) a full v2 effect track — the editor then uses
    /// <see cref="Effects"/> directly; false for a v1/empty document, where the editor seeds defaults via
    /// <see cref="ToEffectTrack"/>.</summary>
    public bool HasEffectTrack { get; }

    /// <summary>The authored zoom, as the standalone track the resolver consumes. Derived from
    /// <see cref="Effects"/> so both shapes expose it identically.</summary>
    public ZoomTrack Zoom => Effects.ZoomTrack;

    /// <summary>Whether the synthetic cursor is shown (the v1 flag). For a v1 document it's the stored value;
    /// for v2 it's derived (a hide span starting at 0 flips it). Default true.</summary>
    public bool ShowCursor { get; }

    /// <summary>Build a v2 edit from a full effect track (the editor's save path).</summary>
    public ClipEdit(EffectTrack effects)
    {
        Effects = effects;
        HasEffectTrack = true;
        ShowCursor = !effects.OfKind<VisibilityEffect>().Any(v => v.StartMs <= 0 && !v.Visible);
    }

    /// <summary>Build a v1-shaped edit (authored zoom + the cursor-shown flag). Kept for the capture-time
    /// default writer and back-compat; the editor migrates it to effects on load.</summary>
    public ClipEdit(ZoomTrack? zoom = null, bool showCursor = true)
    {
        Effects = new EffectTrack((zoom ?? ZoomTrack.Empty).Events.Select(ZoomEffect.FromZoomEvent));
        HasEffectTrack = false;
        ShowCursor = showCursor;
    }

    public static ClipEdit Empty { get; } = new();

    /// <summary>Nothing to persist. v1: no authored zoom and the cursor is shown. v2: no effects at all.</summary>
    public bool IsEmpty => HasEffectTrack ? Effects.IsEmpty : (Zoom.IsEmpty && ShowCursor);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson()
    {
        // A v1-shaped edit still writes v1 (the capture-time default writer stays v1); a full effect track writes v2.
        if (!HasEffectTrack)
        {
            var v1 = new Dto
            {
                V = 1,
                ShowCursor = ShowCursor,
                Zoom = ZoomDtos(Effects),
            };
            return JsonSerializer.Serialize(v1, JsonOptions);
        }

        var dto = new Dto
        {
            V = SchemaVersion,
            Zoom = ZoomDtos(Effects),
            Visibility = Effects.OfKind<VisibilityEffect>()
                .Select(v => new VisibilityDto { Start = v.StartMs, End = v.EndMs, Visible = v.Visible }).ToArray(),
            Ripple = Effects.OfKind<RippleEffect>()
                .Select(r => new RippleDto { Start = r.StartMs, End = r.EndMs }).ToArray(),
            Spotlight = Effects.OfKind<SpotlightEffect>()
                .Select(s => new SpotlightDto
                {
                    Start = s.StartMs, End = s.EndMs, EaseIn = s.EaseInMs, EaseOut = s.EaseOutMs,
                    Color = s.Color, Opacity = s.Opacity, Radius = s.Radius,
                }).ToArray(),
        };
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    private static ZoomDto[] ZoomDtos(EffectTrack effects) => effects.OfKind<ZoomEffect>()
        .Select(z => new ZoomDto
        {
            Start = z.StartMs, End = z.EndMs, Cx = z.CenterX, Cy = z.CenterY,
            Zoom = z.Zoom, EaseIn = z.EaseInMs, EaseOut = z.EaseOutMs,
        }).ToArray();

    /// <summary>Parse an edit document (either on-disk shape); tolerant of nulls/partial data.</summary>
    public static ClipEdit FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<Dto>(json, JsonOptions);
        if (dto is null) return Empty;

        var zoom = (dto.Zoom ?? [])
            .Where(z => z.End > z.Start && z.Zoom > 1)
            .Select(z => new ZoomEvent(z.Start, z.End, z.Cx, z.Cy, z.Zoom, z.EaseIn, z.EaseOut))
            .ToList();

        // v1 (or a file predating the version field): a zoom list + the ShowCursor flag. The editor migrates it.
        if (dto.V < 2) return new ClipEdit(new ZoomTrack(zoom), dto.ShowCursor);

        // v2: a full effect track (drop any malformed span — End must exceed Start).
        var events = new List<EffectEvent>();
        events.AddRange(zoom.Select(ZoomEffect.FromZoomEvent));
        foreach (var v in dto.Visibility ?? [])
            if (v.End > v.Start) events.Add(new VisibilityEffect(v.Start, v.End, v.Visible));
        foreach (var r in dto.Ripple ?? [])
            if (r.End > r.Start) events.Add(new RippleEffect(r.Start, r.End));
        foreach (var s in dto.Spotlight ?? [])
            if (s.End > s.Start) events.Add(new SpotlightEffect(s.Start, s.End, s.EaseIn, s.EaseOut, s.Color, s.Opacity, s.Radius));
        return new ClipEdit(new EffectTrack(events));
    }

    /// <summary>
    /// Project a <b>v1</b> edit onto the unified <see cref="EffectTrack"/> the editor consumes — the forward
    /// migration. Zoom events carry over; the clip-wide <see cref="ShowCursor"/> becomes a single full-length
    /// <see cref="VisibilityEffect"/> so the default shows as an editable block. <paramref name="clipDurationMs"/>
    /// is the clip's source length (a non-positive duration omits the seed). Deterministic and UI-free.
    /// </summary>
    public EffectTrack ToEffectTrack(long clipDurationMs)
    {
        var events = new List<EffectEvent>();
        foreach (var z in Zoom.Events) events.Add(ZoomEffect.FromZoomEvent(z));
        if (clipDurationMs > 0)
            events.Add(new VisibilityEffect(0, clipDurationMs, ShowCursor));
        return new EffectTrack(events);
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

    private sealed class Dto
    {
        public int V { get; set; } = SchemaVersion;
        public bool ShowCursor { get; set; } = true; // v1 only; absent in older files → cursor shown
        public ZoomDto[]? Zoom { get; set; }
        public VisibilityDto[]? Visibility { get; set; }
        public RippleDto[]? Ripple { get; set; }
        public SpotlightDto[]? Spotlight { get; set; }
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

    private sealed class VisibilityDto
    {
        public long Start { get; set; }
        public long End { get; set; }
        public bool Visible { get; set; } = true;
    }

    private sealed class RippleDto
    {
        public long Start { get; set; }
        public long End { get; set; }
    }

    private sealed class SpotlightDto
    {
        public long Start { get; set; }
        public long End { get; set; }
        public long EaseIn { get; set; }
        public long EaseOut { get; set; }
        public string Color { get; set; } = "#FFD24A";
        public double Opacity { get; set; } = 0.30;
        public int Radius { get; set; } = 30;
    }
}
