using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Shrike.Core.Audio;
using Shrike.Core.Recording;

namespace Shrike.App.Controls;

/// <summary>
/// The editable audio lane beneath the scrubber. Unlike the video lanes above it (which share the recording's
/// <b>source</b>-time axis), this lane is drawn on the <b>output</b>-time axis — the timeline you actually hear
/// on export — because audio clips are anchored in output time. Each <see cref="AudioClip"/> is a draggable
/// block: drag the body to move it, drag an edge to crop it in place (trim its in/out point non-destructively),
/// with its sidecar waveform drawn inside. Right-click a block to split it at the playhead, duplicate it, or
/// delete it. Overlapping clips auto-stack onto rows, but the row layout is <b>frozen while you drag</b> so
/// blocks never hop around mid-edit — it re-stacks once the drag ends. Pure view over the window's clip list —
/// it mutates the clips in place and raises <see cref="Changed"/> (move/crop) or the command events; the mix,
/// preview and persistence are the window's job.
/// </summary>
public sealed class WaveformLane : Control
{
    private enum Drag { None, Move, ResizeL, ResizeR }

    private const double GripPx = 8;      // edge zone that crops rather than moves
    private const double SnapPx = 7;      // snap distance in pixels
    private const long MinDurMs = 100;    // a clip can't be cropped shorter than this
    private const double TopPad = 3, RowH = 34, RowGap = 3, BottomPad = 3;

    public Timeline? Timeline { get; set; }

    /// <summary>The clips this lane draws and edits — the window's list, mutated in place.</summary>
    public List<AudioClip> Clips { get; set; } = new();

    public int SelectedIndex { get; private set; } = -1;
    public long PlayheadMs { get; private set; }           // output ms
    public long ViewStartMs { get; private set; }          // output ms
    public long ViewEndMs { get; private set; }            // output ms

    // Decimated peaks (0..1) per sidecar path, over the WHOLE file, with the file's duration for windowing.
    private readonly Dictionary<string, (float[] Peaks, long DurationMs)> _peaks = new();

    private Drag _drag = Drag.None;
    private int _dragIndex = -1;
    private long _grabOffsetMs;   // Move: pointer-to-effective-start offset
    private int[] _rowOf = Array.Empty<int>();
    private int _rowCount = 1;

    public WaveformLane()
    {
        Focusable = false;
        AssignRows();
    }

    /// <summary>Ctrl+wheel: zoom the shared timeline around this <b>source</b> ms. Shift+wheel: pan by the wheel
    /// delta. Mapped to source time so the audio lane drives the same shared view as the ruler / strip / effects.</summary>
    public event Action<long, double>? ZoomRequested;
    public event Action<double>? PanRequested;

    /// <summary>Raised continuously while a clip is dragged (move/crop) — the window updates the preview/inspector.</summary>
    public event Action? Changed;
    /// <summary>Raised once when a move/crop drag ends — the window persists the edit.</summary>
    public event Action? Committed;
    /// <summary>Raised when the selected clip changed (index, or -1 for none).</summary>
    public event Action<int>? SelectionChanged;
    /// <summary>Split the clip at this index at the current playhead.</summary>
    public event Action<int>? SplitRequested;
    /// <summary>Duplicate the clip at this index.</summary>
    public event Action<int>? DuplicateRequested;
    /// <summary>Delete the clip at this index.</summary>
    public event Action<int>? DeleteRequested;

    public void SetClipPeaks(string path, float[] peaks, long fullDurationMs)
    {
        if (string.IsNullOrEmpty(path)) return;
        _peaks[path] = (peaks ?? [], fullDurationMs);
        InvalidateVisual();
    }

    public void ClearPeaks() { _peaks.Clear(); InvalidateVisual(); }

    public void SetView(long startMs, long endMs) { ViewStartMs = startMs; ViewEndMs = endMs; InvalidateVisual(); }
    public void SetPlayhead(long outputMs) { PlayheadMs = outputMs; InvalidateVisual(); }

    /// <summary>Recompute row-stacking + lane height, then redraw. Call after the clip list changes. Skipped
    /// mid-drag so a move/crop doesn't reshuffle rows under the pointer.</summary>
    public void Refresh() { if (_drag == Drag.None) AssignRows(); InvalidateVisual(); }

    public void Select(int index)
    {
        index = index >= 0 && index < Clips.Count ? index : -1;
        if (index == SelectedIndex) return;
        SelectedIndex = index;
        SelectionChanged?.Invoke(index);
        InvalidateVisual();
    }

    // ---- axis helpers (output ms) ----
    // The lane spans [0, AxisSpan]: the kept output duration, stretched if a clip sits past the end so it stays
    // reachable. When no explicit view is set (End <= Start) the whole span is shown.
    private long AxisSpan
    {
        get
        {
            var kept = Timeline is { KeptDurationMs: > 0 } tl ? tl.KeptDurationMs : 0;
            var lastClip = Clips.Count > 0 ? Clips.Max(c => c.EffectiveEndMs) : 0;
            return Math.Max(1, Math.Max(kept, lastClip));
        }
    }
    private double ViewSpan => ViewEndMs > ViewStartMs ? ViewEndMs - ViewStartMs : AxisSpan;
    private double X(long ms) => (ms - ViewStartMs) / ViewSpan * Bounds.Width;
    private long MsAt(double x) => (long)Math.Clamp(ViewStartMs + x / Math.Max(1, Bounds.Width) * ViewSpan, 0, AxisSpan);

    // ---- row stacking (drives lane height) ----
    private void AssignRows()
    {
        _rowOf = new int[Clips.Count];
        // Stack in effective-start order, but keep original indices so hit-testing/selection line up.
        var order = Enumerable.Range(0, Clips.Count)
            .OrderBy(i => Clips[i].EffectiveStartMs).ThenBy(i => Clips[i].EffectiveEndMs);
        var rowEnd = new List<long>();
        foreach (var i in order)
        {
            var c = Clips[i];
            var row = -1;
            for (var r = 0; r < rowEnd.Count; r++) if (rowEnd[r] <= c.EffectiveStartMs) { row = r; break; }
            if (row < 0) { row = rowEnd.Count; rowEnd.Add(0); }
            rowEnd[row] = c.EffectiveEndMs;
            _rowOf[i] = row;
        }
        _rowCount = Math.Max(1, rowEnd.Count);
        Height = TopPad + _rowCount * (RowH + RowGap) - RowGap + BottomPad;
    }

    private Rect RectFor(int i)
    {
        var c = Clips[i];
        var row = i < _rowOf.Length ? _rowOf[i] : 0;
        double left = X(c.EffectiveStartMs), right = X(c.EffectiveEndMs);
        return new Rect(left, TopPad + row * (RowH + RowGap), Math.Max(3, right - left), RowH);
    }

    private long SidecarFullMs(AudioClip c) =>
        _peaks.TryGetValue(c.SidecarPath, out var p) && p.DurationMs > 0
            ? p.DurationMs
            : c.SidecarOffsetMs + c.DurationMs; // unknown → assume the clip already uses the whole file

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Timeline is null || Bounds.Width <= 0) return;
        // Ctrl = zoom the shared timeline (pivot the source ms under the cursor); Shift = pan. Plain wheel is
        // left to the surrounding ScrollViewer. The lane is on output time, so map the pivot back to source.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var pivotSource = Timeline.EditedToSourceMs(MsAt(e.GetPosition(this).X));
            ZoomRequested?.Invoke(pivotSource, e.Delta.Y);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            PanRequested?.Invoke(e.Delta.Y);
            e.Handled = true;
        }
    }

    // ---- input ----
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Bounds.Width <= 0) return;
        var pos = e.GetPosition(this);

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            var hit = HitTest(pos);
            if (hit >= 0) { Select(hit); ShowClipMenu(hit); }
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var idx = HitTest(pos);
        Select(idx);
        if (idx < 0) return;

        var rect = RectFor(idx);
        _dragIndex = idx;
        if (pos.X - rect.X <= GripPx) _drag = Drag.ResizeL;
        else if (rect.Right - pos.X <= GripPx) _drag = Drag.ResizeR;
        else { _drag = Drag.Move; _grabOffsetMs = MsAt(pos.X) - Clips[idx].EffectiveStartMs; }
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == Drag.None || _dragIndex < 0 || _dragIndex >= Clips.Count) return;
        var x = e.GetPosition(this).X;
        var c = Clips[_dragIndex];
        var full = SidecarFullMs(c);

        switch (_drag)
        {
            case Drag.Move:
            {
                var effStart = Snap(MsAt(x) - _grabOffsetMs, skip: _dragIndex);
                var outStart = Math.Max(0, effStart - c.AvOffsetMs);
                Clips[_dragIndex] = c with { OutputStartMs = outStart };
                break;
            }
            case Drag.ResizeL:
            {
                // Move the in-point in place: clamp so we don't cross the right edge or run before the file start.
                var minEff = c.EffectiveStartMs - c.SidecarOffsetMs;          // as far left as the file allows
                var maxEff = c.EffectiveEndMs - MinDurMs;
                var effStart = Math.Clamp(Snap(MsAt(x), skip: _dragIndex), minEff, maxEff);
                var delta = effStart - c.EffectiveStartMs;
                Clips[_dragIndex] = c with
                {
                    OutputStartMs = c.OutputStartMs + delta,
                    SidecarOffsetMs = c.SidecarOffsetMs + delta,
                    DurationMs = c.DurationMs - delta,
                };
                break;
            }
            case Drag.ResizeR:
            {
                var maxEnd = c.EffectiveStartMs + (full - c.SidecarOffsetMs); // can't play past the file
                var effEnd = Math.Clamp(Snap(MsAt(x), skip: _dragIndex), c.EffectiveStartMs + MinDurMs, maxEnd);
                Clips[_dragIndex] = c with { DurationMs = effEnd - c.EffectiveStartMs };
                break;
            }
        }
        // Rows are deliberately NOT reassigned here — the layout is frozen during the drag so blocks don't hop.
        Changed?.Invoke();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        _dragIndex = -1;
        e.Pointer.Capture(null);
        AssignRows();          // settle the row layout now the drag is finished
        InvalidateVisual();
        Committed?.Invoke();
    }

    private int HitTest(Point p)
    {
        if (SelectedIndex >= 0 && SelectedIndex < Clips.Count)
        {
            var r = RectFor(SelectedIndex).Inflate(new Thickness(GripPx, 0));
            if (r.Contains(p)) return SelectedIndex;
        }
        for (var i = 0; i < Clips.Count; i++)
            if (RectFor(i).Contains(p)) return i;
        return -1;
    }

    // Snap an output-ms value to the nearest boundary / playhead / other-clip edge within SnapPx pixels.
    private long Snap(long ms, int skip)
    {
        var bestMs = ms;
        var bestPx = SnapPx;
        void Consider(long target)
        {
            var d = Math.Abs(X(target) - X(ms));
            if (d < bestPx) { bestPx = d; bestMs = target; }
        }
        Consider(0);
        Consider(AxisSpan);
        Consider(PlayheadMs);
        for (var i = 0; i < Clips.Count; i++)
        {
            if (i == skip) continue;
            Consider(Clips[i].EffectiveStartMs);
            Consider(Clips[i].EffectiveEndMs);
        }
        return bestMs;
    }

    public void ShowClipMenu(int index)
    {
        if (index < 0 || index >= Clips.Count) return;
        var flyout = new MenuFlyout();
        void Item(string header, Action act)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => act();
            flyout.Items.Add(mi);
        }
        var atPlayhead = Clips[index].SplitAtOutput(PlayheadMs) is not null;
        var split = new MenuItem { Header = "Split at playhead", IsEnabled = atPlayhead };
        split.Click += (_, _) => SplitRequested?.Invoke(index);
        flyout.Items.Add(split);
        Item("Duplicate", () => DuplicateRequested?.Invoke(index));
        flyout.Items.Add(new Separator());
        Item("Delete", () => DeleteRequested?.Invoke(index));
        flyout.ShowAt(this, showAtPointer: true);
    }

    // ---- render ----
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width; var h = Bounds.Height;
        if (w <= 0) return;

        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#120E08")), new Rect(0, 0, w, h));

        if (Clips.Count == 0)
        {
            var mid = h / 2;
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#2A2318")), 1), new Point(0, mid), new Point(w, mid));
        }
        else
        {
            for (var i = 0; i < Clips.Count; i++) DrawClip(ctx, i);
        }

        var px = X(PlayheadMs);
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#F5A524")), 1), new Point(px, 0), new Point(px, h));
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#322A1E"))), new Rect(0.5, 0.5, w - 1, h - 1));
    }

    private void DrawClip(DrawingContext ctx, int i)
    {
        var c = Clips[i];
        var rect = RectFor(i);
        var selected = i == SelectedIndex;
        var voice = c.Origin == AudioOrigin.EditorVoiceover;

        // Body.
        var fill = new SolidColorBrush(Color.Parse(voice ? (selected ? "#274A63" : "#1E3A4E") : (selected ? "#2F6E67" : "#264F4A")));
        var edge = new Pen(new SolidColorBrush(Color.Parse(selected ? "#F5A524" : voice ? "#5AA6CF" : "#5FA9A1")), selected ? 1.8 : 1);
        var round = new RoundedRect(rect, 5);
        ctx.DrawRectangle(fill, edge, round);

        // Waveform inside the block, windowed to the clip's trimmed span of its sidecar.
        DrawWaveform(ctx, c, rect);

        if (c.Muted)
            ctx.FillRectangle(new SolidColorBrush(Color.Parse("#66140F0A")), rect); // dim a muted clip

        // Crop grips when selected and wide enough — drag these to trim the clip in place.
        if (selected && rect.Width > 2 * GripPx + 4)
        {
            var grip = new SolidColorBrush(Color.Parse("#F5A524"));
            ctx.FillRectangle(grip, new Rect(rect.X + 2, rect.Y + 4, 2, rect.Height - 8));
            ctx.FillRectangle(grip, new Rect(rect.Right - 4, rect.Y + 4, 2, rect.Height - 8));
        }

        // Duration label.
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var label = (voice ? "VO " : "Mic ") + (c.DurationMs / 1000.0).ToString("0.0", inv) + "s";
        var ft = new FormattedText(label, inv, FlowDirection.LeftToRight, Typeface.Default, 10,
            new SolidColorBrush(Color.Parse("#EDE5D6")));
        if (rect.Width > ft.Width + 8)
            ctx.DrawText(ft, new Point(rect.X + 5, rect.Y + 2));
    }

    private void DrawWaveform(DrawingContext ctx, AudioClip c, Rect rect)
    {
        if (!_peaks.TryGetValue(c.SidecarPath, out var p) || p.Peaks.Length == 0 || p.DurationMs <= 0) return;
        using var _ = ctx.PushClip(rect);
        var fill = new SolidColorBrush(Color.Parse(c.Origin == AudioOrigin.EditorVoiceover ? "#7FC7EA" : "#6FD0C6"));
        var mid = rect.Y + rect.Height / 2;
        var maxH = (rect.Height - 8) / 2;
        var x0 = (int)Math.Max(0, Math.Floor(rect.X));
        var x1 = (int)Math.Min(Bounds.Width, Math.Ceiling(rect.Right));
        for (var x = x0; x < x1; x++)
        {
            var outMs = MsAt(x + 0.5);
            var sidecarMs = c.SidecarOffsetMs + (outMs - c.EffectiveStartMs); // where in the file this column is
            if (sidecarMs < 0 || sidecarMs > p.DurationMs) continue;
            var idx = (int)Math.Clamp((double)sidecarMs / p.DurationMs * (p.Peaks.Length - 1), 0, p.Peaks.Length - 1);
            var bh = Math.Max(0.5, p.Peaks[idx] * maxH);
            ctx.FillRectangle(fill, new Rect(x, mid - bh, 1, bh * 2));
        }
    }
}
