namespace Shrike.Core.Recording;

/// <summary>A contiguous span of the source recording, either kept (plays) or cut (skipped).</summary>
public readonly record struct Segment(long StartMs, long EndMs, bool Kept)
{
    public long DurationMs => EndMs - StartMs;

    /// <summary>True if <paramref name="ms"/> lies inside this span (start-inclusive, end-exclusive).</summary>
    public bool Contains(long ms) => ms >= StartMs && ms < EndMs;
}

/// <summary>
/// A non-destructive trim of a <see cref="RecordingSource"/>: a list of kept/cut spans over the source's
/// timeline. Every edit is metadata only — the source file is never touched until export re-encodes the
/// kept ranges. The model is the mathematically minimal one: adjacent spans with the same kept-state are
/// merged, so "which source time plays" is unambiguous and the whole thing is headless-testable.
///
/// <para>Cutting a middle section of a single kept recording yields kept · cut · kept; deleting a segment
/// cuts its span; restoring keeps it again; set-in/out keeps only the chosen window. Playback and export
/// both read the kept ranges back-to-back, so edited time maps onto source time through
/// <see cref="EditedToSourceMs"/> / <see cref="SourceToEditedMs"/>.</para>
/// </summary>
public sealed class Timeline
{
    private readonly List<Segment> _segments = new();

    /// <summary>Total length of the underlying source, in milliseconds.</summary>
    public long DurationMs { get; }

    /// <summary>Raised after any edit that changes the segment list.</summary>
    public event Action? Changed;

    public Timeline(long durationMs)
    {
        if (durationMs <= 0) throw new ArgumentOutOfRangeException(nameof(durationMs), "Duration must be positive.");
        DurationMs = durationMs;
        _segments.Add(new Segment(0, durationMs, Kept: true));
    }

    public Timeline(RecordingSource source) : this(source.DurationMs) { }

    /// <summary>The current spans, ordered and covering [0, <see cref="DurationMs"/>) with no gaps.</summary>
    public IReadOnlyList<Segment> Segments => _segments;

    /// <summary>The kept spans, in order — what playback joins and what export re-encodes.</summary>
    public IReadOnlyList<Segment> KeptRanges => _segments.Where(s => s.Kept).ToList();

    /// <summary>Total kept (playable) time in milliseconds — the length of the edited result.</summary>
    public long KeptDurationMs => _segments.Where(s => s.Kept).Sum(s => s.DurationMs);

    /// <summary>
    /// The kept spans (source time) making up the edited result from <paramref name="editedMs"/> onward —
    /// the first span is clipped to start exactly where playback should resume. Empty at/after the end.
    /// Used to drive playback from an arbitrary position without re-decoding the trimmed-away parts.
    /// </summary>
    public IReadOnlyList<Segment> KeptRangesFrom(long editedMs)
    {
        if (editedMs < 0) editedMs = 0;
        var result = new List<Segment>();
        long acc = 0;
        foreach (var s in _segments)
        {
            if (!s.Kept) continue;
            var end = acc + s.DurationMs;
            if (end > editedMs)
            {
                var start = editedMs > acc ? s.StartMs + (editedMs - acc) : s.StartMs;
                result.Add(new Segment(start, s.EndMs, true));
            }
            acc = end;
        }
        return result;
    }

    /// <summary>False once every span has been cut — export would produce nothing.</summary>
    public bool HasKeptContent => _segments.Any(s => s.Kept);

    /// <summary>Cut the span [<paramref name="fromMs"/>, <paramref name="toMs"/>) — it stops playing/exporting.</summary>
    public void Cut(long fromMs, long toMs) => SetKept(fromMs, toMs, kept: false);

    /// <summary>Restore the span [<paramref name="fromMs"/>, <paramref name="toMs"/>) — it plays again.</summary>
    public void Keep(long fromMs, long toMs) => SetKept(fromMs, toMs, kept: true);

    /// <summary>Set in/out: keep only [<paramref name="fromMs"/>, <paramref name="toMs"/>), cut everything else.</summary>
    public void KeepOnly(long fromMs, long toMs)
    {
        var (a, b) = Clamp(fromMs, toMs);
        if (a >= b) return;
        Rebuild(boundaries: new[] { a, b }, kept: ms => ms >= a && ms < b);
    }

    /// <summary>Cut whichever span contains <paramref name="atMs"/> (delete a segment by pointing at it).</summary>
    public void DeleteSegmentAt(long atMs)
    {
        if (Find(atMs) is { } seg && seg.Kept) SetKept(seg.StartMs, seg.EndMs, kept: false);
    }

    /// <summary>Restore whichever span contains <paramref name="atMs"/> (undo a cut by pointing at it).</summary>
    public void RestoreSegmentAt(long atMs)
    {
        if (Find(atMs) is { } seg && !seg.Kept) SetKept(seg.StartMs, seg.EndMs, kept: true);
    }

    /// <summary>Drop all edits — one kept span covering the whole source again.</summary>
    public void RestoreAll()
    {
        _segments.Clear();
        _segments.Add(new Segment(0, DurationMs, Kept: true));
        Changed?.Invoke();
    }

    /// <summary>The span covering <paramref name="atMs"/>, or null if out of range.</summary>
    public Segment? Find(long atMs)
    {
        foreach (var s in _segments)
            if (s.Contains(atMs)) return s;
        // The end boundary belongs to the last span (so a click at exactly DurationMs resolves).
        return atMs == DurationMs && _segments.Count > 0 ? _segments[^1] : null;
    }

    /// <summary>
    /// Map a position on the edited timeline to the source time that plays there. Clamped to
    /// [0, <see cref="KeptDurationMs"/>]. Returns 0 when nothing is kept.
    /// </summary>
    public long EditedToSourceMs(long editedMs)
    {
        if (editedMs < 0) editedMs = 0;
        long acc = 0;
        foreach (var s in _segments)
        {
            if (!s.Kept) continue;
            if (editedMs < acc + s.DurationMs)
                return s.StartMs + (editedMs - acc);
            acc += s.DurationMs;
        }
        // At or past the end: sit on the last kept frame.
        for (var i = _segments.Count - 1; i >= 0; i--)
            if (_segments[i].Kept) return _segments[i].EndMs;
        return 0;
    }

    /// <summary>
    /// Map a source time to its position on the edited timeline, or null if that time falls in a cut span
    /// (it doesn't appear in the edited result).
    /// </summary>
    public long? SourceToEditedMs(long sourceMs)
    {
        long acc = 0;
        foreach (var s in _segments)
        {
            if (s.Kept && s.Contains(sourceMs))
                return acc + (sourceMs - s.StartMs);
            if (s.Kept) acc += s.DurationMs;
        }
        return null;
    }

    // ---- internals ----

    private void SetKept(long fromMs, long toMs, bool kept)
    {
        var (a, b) = Clamp(fromMs, toMs);
        if (a >= b) return;
        Rebuild(boundaries: new[] { a, b }, kept: ms => (ms >= a && ms < b) ? kept : KeptAtBefore(ms));
    }

    // Rebuild the segment list against a set of new cut points, deciding each resulting span's kept-state
    // from <paramref name="kept"/> (sampled at the span's midpoint), then merging adjacent equal spans.
    private void Rebuild(long[] boundaries, Func<long, bool> kept)
    {
        var cuts = new SortedSet<long> { 0, DurationMs };
        foreach (var s in _segments) cuts.Add(s.StartMs);
        foreach (var b in boundaries) if (b > 0 && b < DurationMs) cuts.Add(b);

        var points = cuts.ToList();
        var rebuilt = new List<Segment>(points.Count);
        for (var i = 0; i < points.Count - 1; i++)
        {
            long start = points[i], end = points[i + 1];
            if (end <= start) continue;
            var mid = start + (end - start) / 2;
            var span = new Segment(start, end, kept(mid));
            // Coalesce with the previous span when they share a kept-state.
            if (rebuilt.Count > 0 && rebuilt[^1].Kept == span.Kept)
                rebuilt[^1] = rebuilt[^1] with { EndMs = end };
            else
                rebuilt.Add(span);
        }

        _segments.Clear();
        _segments.AddRange(rebuilt);
        Changed?.Invoke();
    }

    // Current kept-state at a point, used to preserve spans outside the range being edited.
    private bool KeptAtBefore(long ms)
    {
        foreach (var s in _segments)
            if (s.Contains(ms)) return s.Kept;
        return _segments.Count > 0 && _segments[^1].Kept;
    }

    private (long, long) Clamp(long fromMs, long toMs)
    {
        if (toMs < fromMs) (fromMs, toMs) = (toMs, fromMs);
        return (Math.Clamp(fromMs, 0, DurationMs), Math.Clamp(toMs, 0, DurationMs));
    }
}
