using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Shrike.Core.Recording;

namespace Shrike.App.Controls;

/// <summary>
/// The unified effects lane beneath the scrubber — one timeline holding every effect kind (zoom, spotlight,
/// click-ripple, mouse-visibility, canvas) as draggable / resizable blocks. Shares the scrubber's
/// <b>source-time</b> x-axis so a block lines up with the video under it. Overlapping effects auto-stack onto
/// rows (greedy: each block takes the first row free at its start), and recorded mouse clicks show as ticks on
/// a thin strip pinned at the bottom (drag edges snap to them). Right-click adds an effect at the click point.
/// It edits the window's <see cref="EffectEvent"/> list in place and raises <see cref="Changed"/> as timing
/// changes; per-kind properties (zoom amount, target box, …) are set elsewhere. Pure view over the model — it
/// never resolves viewports. Generalises the old zoom-only lane.
/// </summary>
public sealed class EffectsLane : Control
{
    private enum Drag { None, Move, ResizeL, ResizeR }

    private const double GripPx = 8;      // edge zone that resizes rather than moves
    private const double SnapPx = 7;      // snap distance in pixels
    private const long MinDurMs = 200;    // an effect can't be shorter than this

    private const double TopPad = 3, RowH = 20, RowGap = 3, ClickStripH = 9, BottomPad = 2;

    public Timeline? Timeline { get; set; }
    public List<EffectEvent> Events { get; set; } = new();
    public IReadOnlyList<long> ClickMarks { get; set; } = Array.Empty<long>(); // source ms of clicks
    public int SelectedIndex { get; private set; } = -1;
    public long PlayheadMs { get; private set; }

    /// <summary>The visible time window (source ms). When End &lt;= Start the whole clip is shown.</summary>
    public long ViewStartMs { get; private set; }
    public long ViewEndMs { get; private set; }
    public void SetView(long startMs, long endMs) { ViewStartMs = startMs; ViewEndMs = endMs; InvalidateVisual(); }

    /// <summary>Ctrl+wheel: zoom the view around this source ms. Shift+wheel: pan by this wheel delta.</summary>
    public event Action<long, double>? ZoomRequested;
    public event Action<double>? PanRequested;

    /// <summary>Raised when an effect's timing changed (drag/resize) — the window re-resolves + persists.</summary>
    public event Action? Changed;
    /// <summary>Raised when the selected effect changed (index, or -1 for none).</summary>
    public event Action<int>? SelectionChanged;
    /// <summary>Add a new effect of this kind anchored at this source ms (from the right-click menu or the button).</summary>
    public event Action<EffectKind, long>? AddRequested;
    /// <summary>Delete the effect at this index (from the right-click menu).</summary>
    public event Action<int>? DeleteRequested;

    private Drag _drag = Drag.None;
    private int _dragIndex = -1;
    private long _grabOffsetMs;   // for Move: pointer-to-start offset
    private int[] _rowOf = Array.Empty<int>();
    private int _rowCount = 1;

    public EffectsLane()
    {
        Focusable = false;
        AssignRows();
    }

    public void SetPlayhead(long sourceMs) { PlayheadMs = sourceMs; InvalidateVisual(); }

    public void Select(int index)
    {
        index = index >= 0 && index < Events.Count ? index : -1;
        if (index == SelectedIndex) return;
        SelectedIndex = index;
        SelectionChanged?.Invoke(index);
        InvalidateVisual();
    }

    /// <summary>Recompute the row-stacking + lane height, then redraw. Call after the event list changes.
    /// Skipped mid-drag so a move/resize doesn't reshuffle rows under the pointer (they settle on release).</summary>
    public void Refresh() { if (_drag == Drag.None) AssignRows(); InvalidateVisual(); }

    // ---- axis helpers ----
    private double Dur => Timeline is { DurationMs: > 0 } tl ? tl.DurationMs : 1;
    private double ViewSpan => ViewEndMs > ViewStartMs ? ViewEndMs - ViewStartMs : Dur;
    private double X(long ms) => (ms - ViewStartMs) / ViewSpan * Bounds.Width;
    private long MsAt(double x) => (long)Math.Clamp(ViewStartMs + x / Math.Max(1, Bounds.Width) * ViewSpan, 0, Dur);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Timeline is null || Bounds.Width <= 0) return;
        // Ctrl = zoom the timeline, Shift = pan; plain wheel is left to the surrounding ScrollViewer (vertical).
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) { ZoomRequested?.Invoke(MsAt(e.GetPosition(this).X), e.Delta.Y); e.Handled = true; }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { PanRequested?.Invoke(e.Delta.Y); e.Handled = true; }
    }

    // ---- row stacking (width-independent; drives lane height) ----
    private void AssignRows()
    {
        _rowOf = new int[Events.Count];
        var rowEnd = new List<long>();
        foreach (var i in Enumerable.Range(0, Events.Count).OrderBy(i => Events[i].StartMs).ThenBy(i => Events[i].EndMs))
        {
            var ev = Events[i];
            var row = -1;
            for (var r = 0; r < rowEnd.Count; r++) if (rowEnd[r] <= ev.StartMs) { row = r; break; }
            if (row < 0) { row = rowEnd.Count; rowEnd.Add(0); }
            rowEnd[row] = ev.EndMs;
            _rowOf[i] = row;
        }
        _rowCount = Math.Max(1, rowEnd.Count);
        Height = TopPad + _rowCount * (RowH + RowGap) - RowGap + ClickStripH + BottomPad;
    }

    private Rect RectFor(int i)
    {
        var ev = Events[i];
        var row = i < _rowOf.Length ? _rowOf[i] : 0;
        double left = X(ev.StartMs), right = X(ev.EndMs);
        return new Rect(left, TopPad + row * (RowH + RowGap), Math.Max(3, right - left), RowH);
    }

    // ---- input ----
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Timeline is null || Bounds.Width <= 0) return;
        var pos = e.GetPosition(this);

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            ShowAddMenu(this, atPointer: true, MsAt(pos.X), HitTest(pos));
            return;
        }

        if (e.ClickCount == 2) { if (HitTest(pos) < 0) AddRequested?.Invoke(EffectKind.Zoom, MsAt(pos.X)); return; }

        var hit = HitTest(pos);
        Select(hit);
        if (hit < 0) return;

        // Decide move vs resize from where in the block the press landed.
        var rect = RectFor(hit);
        _dragIndex = hit;
        if (pos.X - rect.X <= GripPx) _drag = Drag.ResizeL;
        else if (rect.Right - pos.X <= GripPx) _drag = Drag.ResizeR;
        else { _drag = Drag.Move; _grabOffsetMs = MsAt(pos.X) - Events[hit].StartMs; }
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == Drag.None || _dragIndex < 0) return;
        var x = e.GetPosition(this).X;
        var ev = Events[_dragIndex];
        var full = (long)Dur;

        switch (_drag)
        {
            case Drag.Move:
            {
                var dur = ev.EndMs - ev.StartMs;
                var start = Math.Clamp(MsAt(x) - _grabOffsetMs, 0, full - dur);
                start = Snap(start, skip: _dragIndex);            // snap the leading edge
                start = Math.Clamp(start, 0, full - dur);
                Events[_dragIndex] = ev with { StartMs = start, EndMs = start + dur };
                break;
            }
            case Drag.ResizeL:
            {
                var start = Math.Clamp(Snap(MsAt(x), skip: _dragIndex), 0, ev.EndMs - MinDurMs);
                Events[_dragIndex] = ev with { StartMs = start };
                break;
            }
            case Drag.ResizeR:
            {
                var end = Math.Clamp(Snap(MsAt(x), skip: _dragIndex), ev.StartMs + MinDurMs, full);
                Events[_dragIndex] = ev with { EndMs = end };
                break;
            }
        }
        // Rows are frozen during the drag so blocks don't hop between rows under the pointer; they re-stack on
        // release. AssignRows() is skipped here on purpose.
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
        AssignRows();       // settle the row layout now the drag is finished
        InvalidateVisual();
    }

    // Index of the effect whose block contains the point, or -1. Prefers the selected effect when blocks overlap.
    private int HitTest(Point p)
    {
        if (SelectedIndex >= 0 && SelectedIndex < Events.Count)
        {
            var r = RectFor(SelectedIndex).Inflate(new Thickness(GripPx, 0));
            if (r.Contains(p)) return SelectedIndex;
        }
        for (var i = 0; i < Events.Count; i++)
            if (RectFor(i).Contains(p)) return i;
        return -1;
    }

    // Snap a source-ms value to the nearest click / boundary / other-event edge within SnapPx pixels.
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
        Consider((long)Dur);
        foreach (var c in ClickMarks) Consider(c);
        for (var i = 0; i < Events.Count; i++)
        {
            if (i == skip) continue;
            Consider(Events[i].StartMs);
            Consider(Events[i].EndMs);
        }
        return bestMs;
    }

    /// <summary>Open the "add effect" menu anchored to <paramref name="anchor"/> (at the pointer, or below the
    /// control), placing whatever's chosen at <paramref name="sourceMs"/>. When <paramref name="hitIndex"/> ≥ 0
    /// the menu also offers to delete the block under the cursor.</summary>
    public void ShowAddMenu(Control anchor, bool atPointer, long sourceMs, int hitIndex)
    {
        var flyout = new MenuFlyout();
        void AddItem(string header, EffectKind kind)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => AddRequested?.Invoke(kind, sourceMs);
            flyout.Items.Add(mi);
        }
        AddItem("Zoom", EffectKind.Zoom);
        AddItem("Spotlight", EffectKind.Spotlight);
        AddItem("Click ripple", EffectKind.Ripple);
        AddItem("Mouse visibility", EffectKind.Visibility);
        AddItem("Canvas", EffectKind.Canvas);
        if (hitIndex >= 0 && hitIndex < Events.Count)
        {
            flyout.Items.Add(new Separator());
            var del = new MenuItem { Header = "Delete" };
            del.Click += (_, _) => DeleteRequested?.Invoke(hitIndex);
            flyout.Items.Add(del);
        }
        flyout.ShowAt(anchor, showAtPointer: atPointer);
    }

    // ---- render ----
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width; var h = Bounds.Height;
        if (Timeline is null || w <= 0) return;

        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#120E08")), new Rect(0, 0, w, h));

        // Dim cut spans so the lane reads against the scrubber above it.
        var dim = new SolidColorBrush(Color.Parse("#80140F0A"));
        foreach (var s in Timeline.Segments)
            if (!s.Kept) ctx.FillRectangle(dim, new Rect(X(s.StartMs), 0, Math.Max(1, X(s.EndMs) - X(s.StartMs)), h));

        // Effect blocks, one per row band.
        for (var i = 0; i < Events.Count; i++) DrawBlock(ctx, i);

        // Click markers: little amber ticks along the bottom strip.
        var tick = new SolidColorBrush(Color.Parse("#80F5A524"));
        foreach (var c in ClickMarks)
            ctx.FillRectangle(tick, new Rect(X(c) - 0.5, h - ClickStripH + 1, 1.5, ClickStripH - 2));

        // Playhead.
        var amber = new SolidColorBrush(Color.Parse("#F5A524"));
        var px = X(PlayheadMs);
        ctx.DrawLine(new Pen(amber, 1), new Point(px, 0), new Point(px, h));

        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#322A1E"))), new Rect(0.5, 0.5, w - 1, h - 1));
    }

    private void DrawBlock(DrawingContext ctx, int i)
    {
        var ev = Events[i];
        var rect = RectFor(i);
        var selected = i == SelectedIndex;
        var (fill, fillSel, edge) = Palette(ev.Kind);

        var brush = new SolidColorBrush(Color.Parse(selected ? fillSel : fill));
        var border = new Pen(new SolidColorBrush(Color.Parse(selected ? "#F5A524" : edge)), selected ? 1.8 : 1);
        ctx.DrawRectangle(brush, border, new RoundedRect(rect, 5));

        // Resize grips.
        var grip = new SolidColorBrush(Color.Parse(selected ? "#F5A524" : edge));
        if (rect.Width > 2 * GripPx + 4)
        {
            ctx.FillRectangle(grip, new Rect(rect.X + 2, rect.Y + 3, 2, rect.Height - 6));
            ctx.FillRectangle(grip, new Rect(rect.Right - 4, rect.Y + 3, 2, rect.Height - 6));
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var textBrush = new SolidColorBrush(Color.Parse("#EDE5D6"));
        var label = Label(ev);
        var ft = new FormattedText(label, inv, FlowDirection.LeftToRight, Typeface.Default, 11, textBrush);
        if (rect.Width <= ft.Width + 10) // fall back to the short label when the block is narrow
            ft = new FormattedText(ShortLabel(ev), inv, FlowDirection.LeftToRight, Typeface.Default, 11, textBrush);
        if (rect.Width > ft.Width + 8)
            ctx.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
    }

    // (fill, fill-when-selected, border) per kind.
    private static (string, string, string) Palette(EffectKind kind) => kind switch
    {
        EffectKind.Zoom       => ("#264F4A", "#2F6E67", "#5FA9A1"),
        EffectKind.Spotlight  => ("#463A18", "#5A4A1E", "#D9B04A"),
        EffectKind.Ripple     => ("#1E3A4E", "#274A63", "#5AA6CF"),
        EffectKind.Visibility => ("#2E2E36", "#3A3A44", "#8A8A9A"),
        EffectKind.Canvas     => ("#3A2548", "#4A2F5E", "#B07AD0"),
        _                     => ("#264F4A", "#2F6E67", "#5FA9A1"),
    };

    private static string Label(EffectEvent ev)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var secs = (ev.DurationMs / 1000.0).ToString("0.0#", inv) + "s";
        return ev switch
        {
            ZoomEffect z      => z.Zoom.ToString("0.0#", inv) + "× · " + secs,
            SpotlightEffect   => "Spotlight · " + secs,
            RippleEffect      => "Ripple · " + secs,
            VisibilityEffect v => (v.Visible ? "Cursor shown" : "Cursor hidden") + " · " + secs,
            CanvasEffect c    => "Canvas (" + (c.Space == CanvasSpace.Screen ? "screen" : "content") + ") · " + secs,
            _                 => secs,
        };
    }

    private static string ShortLabel(EffectEvent ev)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return ev switch
        {
            ZoomEffect z      => z.Zoom.ToString("0.0#", inv) + "×",
            SpotlightEffect   => "Spot",
            RippleEffect      => "Ripple",
            VisibilityEffect v => v.Visible ? "Show" : "Hide",
            CanvasEffect      => "Canvas",
            _                 => "",
        };
    }
}
