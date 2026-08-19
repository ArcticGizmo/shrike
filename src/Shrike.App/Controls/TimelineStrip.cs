using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Shrike.Core.Recording;

namespace Shrike.App.Controls;

/// <summary>
/// The scrubber/trim lane: a filmstrip divided into segments by draggable boundary handles, each span kept or
/// cut. Trim the ends by dragging the amber end-handles; drag any interior boundary to move it; drag across a
/// span to select a quick range; double-click a span to edit it in the pane; Ctrl+drag drops a new split;
/// right-click for Split / Cut / Keep / Remove-split; a plain click seeks. It shares the ruler's view window
/// (Ctrl/Shift+wheel zoom/pan). Pure view — it reads the <see cref="Timeline"/> for rendering + hit-testing
/// but never edits it; every edit goes back to the window through events.
/// </summary>
public sealed class TimelineStrip : Control
{
    private enum Mode { None, TrimHead, TrimTail, MoveBoundary, SelectPending, Select }

    private const double HandlePx = 9;        // grab zone / draw width for a handle
    private const double DragThresholdPx = 4; // move less than this and a body press is a click, not a selection

    private readonly List<(long Ms, Bitmap Thumb)> _thumbs = new();
    private Mode _mode;
    private double _pressX;
    private long _dragBoundaryMs;   // the interior boundary currently being dragged
    private long? _selA, _selB;     // the drag-selection range (source ms)

    public Timeline? Timeline { get; set; }
    public long PlayheadMs { get; private set; }

    public long ViewStartMs { get; private set; }
    public long ViewEndMs { get; private set; }
    public void SetView(long startMs, long endMs) { ViewStartMs = startMs; ViewEndMs = endMs; InvalidateVisual(); }

    /// <summary>A plain click — seek here.</summary>
    public event Action<long>? Seeked;
    /// <summary>Double-click a span — select it for editing in the pane (its start/end).</summary>
    public event Action<long, long>? SegmentSelected;
    /// <summary>A quick drag-selection range (min, max) — the window offers Cut/Keep for it.</summary>
    public event Action<long, long>? RangeSelected;
    public event Action? SelectionCleared;
    /// <summary>Drag the head/tail end-handle to this source ms.</summary>
    public event Action<long>? TrimHeadTo;
    public event Action<long>? TrimTailTo;
    /// <summary>Drag an interior boundary from → to.</summary>
    public event Action<long, long>? BoundaryMoved;
    /// <summary>Right-click / Ctrl+drag — add a split at this source ms.</summary>
    public event Action<long>? SplitRequested;
    /// <summary>Right-click a span — set its kept-state (the quick Cut/Keep; keeping a cut restores it).</summary>
    public event Action<long, bool>? SetKeptRequested;
    /// <summary>Right-click near a split — remove it.</summary>
    public event Action<long>? RemoveSplitRequested;
    public event Action<long, double>? ZoomRequested;
    public event Action<double>? PanRequested;

    public TimelineStrip()
    {
        Height = 76;
        Focusable = false;
    }

    public void SetPlayhead(long ms) { PlayheadMs = ms; InvalidateVisual(); }
    public void AddThumbnail(long ms, Bitmap thumb) { _thumbs.Add((ms, thumb)); InvalidateVisual(); }
    public void Refresh() => InvalidateVisual();
    public void ClearSelection() { _selA = _selB = null; InvalidateVisual(); }
    public (long A, long B)? Selection => _selA is { } a && _selB is { } b && a != b ? (Math.Min(a, b), Math.Max(a, b)) : null;
    public double MsToX(long ms) => Xp(ms);

    // ---- axis / view ----
    private double Dur => Timeline is { DurationMs: > 0 } tl ? tl.DurationMs : 1;
    private double ViewSpan => ViewEndMs > ViewStartMs ? ViewEndMs - ViewStartMs : Dur;
    private double Xp(long ms) => (ms - ViewStartMs) / ViewSpan * Bounds.Width;
    private long MsAt(double x) => (long)Math.Clamp(ViewStartMs + x / Math.Max(1, Bounds.Width) * ViewSpan, 0, Dur);
    private long PxToMs(double px) => (long)(px / Math.Max(1, Bounds.Width) * ViewSpan); // a pixel distance in ms

    private (long Start, long End)? KeptExtent()
    {
        var kr = Timeline?.KeptRanges;
        if (kr is null || kr.Count == 0) return null;
        return (kr[0].StartMs, kr[^1].EndMs);
    }

    // Interior boundaries (segment starts strictly inside the clip) — the draggable split/cut edges.
    private IEnumerable<long> InteriorBoundaries()
        => Timeline is null ? [] : Timeline.Segments.Select(s => s.StartMs).Where(m => m > 0);

    // ---- input ----
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Timeline is null || Bounds.Width <= 0) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) { ZoomRequested?.Invoke(MsAt(e.GetPosition(this).X), e.Delta.Y); e.Handled = true; }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { PanRequested?.Invoke(e.Delta.Y); e.Handled = true; }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Timeline is null || Bounds.Width <= 0) return;
        var x = e.GetPosition(this).X;

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) { ShowContextMenu(x); return; }

        // Ctrl+drag: drop a split here and drag it.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var ms = MsAt(x);
            SplitRequested?.Invoke(ms);
            _mode = Mode.MoveBoundary; _dragBoundaryMs = ms;
            e.Pointer.Capture(this);
            return;
        }

        // Double-click a span → select it for pane editing.
        if (e.ClickCount == 2)
        {
            if (Timeline.Find(MsAt(x)) is { } seg) SegmentSelected?.Invoke(seg.StartMs, seg.EndMs);
            return;
        }

        // Grab a handle if near one — trim handles at the kept-extent ends take precedence over interior ones.
        if (KeptExtent() is { } ext)
        {
            if (Math.Abs(x - Xp(ext.Start)) <= HandlePx) { _mode = Mode.TrimHead; e.Pointer.Capture(this); return; }
            if (Math.Abs(x - Xp(ext.End)) <= HandlePx) { _mode = Mode.TrimTail; e.Pointer.Capture(this); return; }
        }
        foreach (var b in InteriorBoundaries())
            if (Math.Abs(x - Xp(b)) <= HandlePx) { _mode = Mode.MoveBoundary; _dragBoundaryMs = b; e.Pointer.Capture(this); return; }

        // Otherwise a select-or-seek gesture on the body.
        _mode = Mode.SelectPending;
        _pressX = x;
        _selA = MsAt(x);
        _selB = null;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_mode == Mode.None) return;
        var x = e.GetPosition(this).X;

        switch (_mode)
        {
            case Mode.TrimHead: TrimHeadTo?.Invoke(MsAt(x)); break;
            case Mode.TrimTail: TrimTailTo?.Invoke(MsAt(x)); break;
            case Mode.MoveBoundary:
                var to = ClampBetweenNeighbours(_dragBoundaryMs, MsAt(x));
                if (to != _dragBoundaryMs) { BoundaryMoved?.Invoke(_dragBoundaryMs, to); _dragBoundaryMs = to; }
                break;
            case Mode.SelectPending:
                if (Math.Abs(x - _pressX) < DragThresholdPx) break;
                _mode = Mode.Select;
                goto case Mode.Select;
            case Mode.Select:
                _selB = MsAt(x);
                if (Selection is { } sel) RangeSelected?.Invoke(sel.A, sel.B);
                InvalidateVisual();
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var was = _mode;
        _mode = Mode.None;
        e.Pointer.Capture(null);

        if (was == Mode.SelectPending) // a click: seek and drop any selection
        {
            _selA = _selB = null;
            SelectionCleared?.Invoke();
            Seeked?.Invoke(MsAt(e.GetPosition(this).X));
            InvalidateVisual();
        }
    }

    // Keep a dragged boundary strictly between its neighbours (min 100ms spans).
    private long ClampBetweenNeighbours(long boundary, long want)
    {
        if (Timeline is null) return want;
        long lo = 0, hi = Timeline.DurationMs;
        foreach (var b in InteriorBoundaries())
        {
            if (b < boundary) lo = Math.Max(lo, b);
            else if (b > boundary) hi = Math.Min(hi, b);
        }
        return Math.Clamp(want, lo + 100, hi - 100);
    }

    private void ShowContextMenu(double x)
    {
        if (Timeline is null) return;
        var ms = MsAt(x);
        var tol = Math.Max(1, PxToMs(HandlePx));
        var flyout = new MenuFlyout();
        void Item(string header, Action act) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => act(); flyout.Items.Add(mi); }

        Item("Split here", () => SplitRequested?.Invoke(ms));
        if (Timeline.Find(ms) is { } seg)
        {
            flyout.Items.Add(new Separator());
            if (seg.Kept) Item("Cut this span", () => SetKeptRequested?.Invoke(ms, false));
            else Item("Keep this span", () => SetKeptRequested?.Invoke(ms, true));
        }
        if (Timeline.HasSplitNear(ms, tol))
        {
            flyout.Items.Add(new Separator());
            Item("Remove split", () => RemoveSplitRequested?.Invoke(ms));
        }
        flyout.ShowAt(this, showAtPointer: true);
    }

    // ---- render ----
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width;
        var h = Bounds.Height;
        var tl = Timeline;
        if (tl is null || w <= 0) return;

        double X(long ms) => Xp(ms);

        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#0E0B06")), new Rect(0, 0, w, h));

        // Filmstrip: each thumbnail occupies the slice around its timestamp, positioned by time (tracks zoom/pan).
        if (_thumbs.Count > 0)
        {
            var half = Dur / _thumbs.Count / 2.0;
            foreach (var (ms, thumb) in _thumbs)
            {
                var left = X((long)(ms - half));
                var right = X((long)(ms + half));
                if (right < 0 || left > w) continue;
                var dest = new Rect(left, 0, Math.Max(1, right - left), h);
                using (ctx.PushClip(dest))
                    ctx.DrawImage(thumb, Fit(thumb, dest));
            }
        }

        // Dim the cut spans and mark them with a red top rule.
        var dim = new SolidColorBrush(Color.Parse("#B0140F0A"));
        var cutRule = new SolidColorBrush(Color.Parse("#EF4444"));
        foreach (var s in tl.Segments)
        {
            if (s.Kept) continue;
            var r = new Rect(X(s.StartMs), 0, Math.Max(1, X(s.EndMs) - X(s.StartMs)), h);
            ctx.FillRectangle(dim, r);
            ctx.FillRectangle(cutRule, new Rect(r.X, 0, r.Width, 2));
        }

        // Drag-selection band.
        if (Selection is { } sel)
        {
            var r = new Rect(X(sel.A), 0, Math.Max(1, X(sel.B) - X(sel.A)), h);
            ctx.FillRectangle(new SolidColorBrush(Color.Parse("#3538BDF8")), r);
            var pen = new Pen(new SolidColorBrush(Color.Parse("#38BDF8")), 1.5);
            ctx.DrawLine(pen, new Point(r.X, 0), new Point(r.X, h));
            ctx.DrawLine(pen, new Point(r.Right, 0), new Point(r.Right, h));
        }

        var ext = KeptExtent();

        // Interior boundary handles (skip the ones that are the trim-extent edges — those get end-handles).
        foreach (var b in InteriorBoundaries())
        {
            if (ext is { } ex && (b == ex.Start || b == ex.End)) continue;
            DrawBoundary(ctx, X(b), h);
        }

        // Trim handles at the kept-region ends.
        if (ext is { } e2)
        {
            DrawHandle(ctx, X(e2.Start), h, left: true);
            DrawHandle(ctx, X(e2.End), h, left: false);
        }

        // Playhead: amber line + top triangle.
        var px = X(PlayheadMs);
        var amber = new SolidColorBrush(Color.Parse("#F5A524"));
        ctx.DrawLine(new Pen(amber, 2), new Point(px, 0), new Point(px, h));
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(new Point(px - 5, 0), true);
            g.LineTo(new Point(px + 5, 0));
            g.LineTo(new Point(px, 7));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(amber, null, tri);

        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#322A1E"))), new Rect(0.5, 0.5, w - 1, h - 1));
    }

    // A grabbable end (trim) handle: a rounded bar with a bracket lip pointing inward.
    private static void DrawHandle(DrawingContext ctx, double x, double h, bool left)
    {
        var fill = new SolidColorBrush(Color.Parse("#F5A524"));
        var dir = left ? 1 : -1;
        var barX = left ? x : x - 5;
        ctx.DrawRectangle(fill, null, new RoundedRect(new Rect(barX, 0, 5, h), 2));
        var grip = new Pen(new SolidColorBrush(Color.Parse("#140F0A")), 1);
        ctx.DrawLine(grip, new Point(barX + 2, h * 0.35), new Point(barX + 2, h * 0.65));
        ctx.DrawLine(new Pen(fill, 2), new Point(x, 1), new Point(x + dir * 6, 1));
        ctx.DrawLine(new Pen(fill, 2), new Point(x, h - 1), new Point(x + dir * 6, h - 1));
    }

    // An interior boundary handle: a thin line with a grab pill at the vertical centre.
    private static void DrawBoundary(DrawingContext ctx, double x, double h)
    {
        var amber = new SolidColorBrush(Color.Parse("#F5A524"));
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#C0F5A524")), 1), new Point(x, 0), new Point(x, h));
        var pill = new Rect(x - 3, h / 2 - 11, 6, 22);
        ctx.DrawRectangle(amber, new Pen(new SolidColorBrush(Color.Parse("#140F0A")), 1), new RoundedRect(pill, 3));
        var grip = new Pen(new SolidColorBrush(Color.Parse("#140F0A")), 1);
        ctx.DrawLine(grip, new Point(x, h / 2 - 5), new Point(x, h / 2 + 5));
    }

    // Center-crop the thumbnail to fill its slot without distortion.
    private static Rect Fit(Bitmap bmp, Rect dest)
    {
        var scale = Math.Max(dest.Width / bmp.PixelSize.Width, dest.Height / bmp.PixelSize.Height);
        var dw = bmp.PixelSize.Width * scale;
        var dh = bmp.PixelSize.Height * scale;
        return new Rect(dest.X + (dest.Width - dw) / 2, dest.Y + (dest.Height - dh) / 2, dw, dh);
    }
}
