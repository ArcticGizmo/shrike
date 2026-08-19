using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Shrike.Core.Recording;

namespace Shrike.App.Controls;

/// <summary>
/// The scrubber/trim lane: a filmstrip of thumbnails across the source, with cut spans dimmed. It's where you
/// crop the clip directly — drag the <b>trim handles</b> at each end of the kept region inward to trim
/// head/tail, or <b>drag across the body</b> to select a range (the window then offers Cut / Keep). A plain
/// click seeks. Sharing the ruler's view window, it zooms/pans with Ctrl/Shift+wheel. Pure view: it holds a
/// reference to the <see cref="Timeline"/> for rendering + handle positions but never edits it — all edits go
/// back to the window through events.
/// </summary>
public sealed class TimelineStrip : Control
{
    private enum Mode { None, TrimHead, TrimTail, SelectPending, Select }

    private const double HandlePx = 9;      // grab zone / draw width for the trim handles
    private const double DragThresholdPx = 4; // move less than this and it's a click (seek), not a selection

    private readonly List<(long Ms, Bitmap Thumb)> _thumbs = new();
    private Mode _mode;
    private double _pressX;
    private long? _selA, _selB;             // the drag-selection range (source ms)

    public Timeline? Timeline { get; set; }
    public long PlayheadMs { get; private set; }

    /// <summary>The visible time window (source ms). When End &lt;= Start the whole clip is shown.</summary>
    public long ViewStartMs { get; private set; }
    public long ViewEndMs { get; private set; }
    public void SetView(long startMs, long endMs) { ViewStartMs = startMs; ViewEndMs = endMs; InvalidateVisual(); }

    /// <summary>Raised when the pointer is released on a plain click — the committed seek position.</summary>
    public event Action<long>? Seeked;
    /// <summary>Raised as a range is drag-selected (min, max source ms); the window shows Cut/Keep for it.</summary>
    public event Action<long, long>? RangeSelected;
    /// <summary>Raised when the selection is dropped (a plain click, or Esc).</summary>
    public event Action? SelectionCleared;
    /// <summary>Drag the head/tail trim handle to this source ms (set the kept region's start/end).</summary>
    public event Action<long>? TrimHeadTo;
    public event Action<long>? TrimTailTo;
    /// <summary>Right-click a cut (dimmed) span — restore it.</summary>
    public event Action<long>? RestoreRequested;
    /// <summary>Ctrl+wheel: zoom the view around this source ms. Shift+wheel: pan by this wheel delta.</summary>
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

    /// <summary>Drop any drag-selection (after a Cut/Keep, or on cancel).</summary>
    public void ClearSelection() { _selA = _selB = null; InvalidateVisual(); }

    /// <summary>The current selection as (min, max) source ms, or null.</summary>
    public (long A, long B)? Selection => _selA is { } a && _selB is { } b && a != b ? (Math.Min(a, b), Math.Max(a, b)) : null;

    /// <summary>Pixel x of a source ms in the current view — lets the window place the floating Cut/Keep bar.</summary>
    public double MsToX(long ms) => Xp(ms);

    // ---- axis / view ----
    private double Dur => Timeline is { DurationMs: > 0 } tl ? tl.DurationMs : 1;
    private double ViewSpan => ViewEndMs > ViewStartMs ? ViewEndMs - ViewStartMs : Dur;
    private double Xp(long ms) => (ms - ViewStartMs) / ViewSpan * Bounds.Width;
    private long MsAt(double x) => (long)Math.Clamp(ViewStartMs + x / Math.Max(1, Bounds.Width) * ViewSpan, 0, Dur);

    private (long Start, long End)? KeptExtent()
    {
        var kr = Timeline?.KeptRanges;
        if (kr is null || kr.Count == 0) return null;
        return (kr[0].StartMs, kr[^1].EndMs);
    }

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

        // Right-click a cut span → restore it.
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            if (Timeline.Find(MsAt(x)) is { Kept: false }) RestoreRequested?.Invoke(MsAt(x));
            return;
        }

        // Grab a trim handle if the press is near a kept-extent edge.
        if (KeptExtent() is { } ext)
        {
            if (Math.Abs(x - Xp(ext.Start)) <= HandlePx) { _mode = Mode.TrimHead; e.Pointer.Capture(this); return; }
            if (Math.Abs(x - Xp(ext.End)) <= HandlePx) { _mode = Mode.TrimTail; e.Pointer.Capture(this); return; }
        }

        // Otherwise start a select-or-seek gesture.
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

        if (was == Mode.SelectPending) // never crossed the threshold → a click: seek, and drop any selection
        {
            _selA = _selB = null;
            SelectionCleared?.Invoke();
            Seeked?.Invoke(MsAt(e.GetPosition(this).X));
            InvalidateVisual();
        }
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

        // Filmstrip: each thumbnail occupies the slice of the strip around its timestamp, positioned by time so
        // it tracks the zoom/pan of the view window (off-screen slices are skipped).
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

        // Trim handles at the kept-region edges.
        if (KeptExtent() is { } ext)
        {
            DrawHandle(ctx, X(ext.Start), h, left: true);
            DrawHandle(ctx, X(ext.End), h, left: false);
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

        // Frame.
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#322A1E"))), new Rect(0.5, 0.5, w - 1, h - 1));
    }

    // A grabbable trim handle at the kept-region edge: a rounded bar with a bracket lip pointing inward.
    private static void DrawHandle(DrawingContext ctx, double x, double h, bool left)
    {
        var fill = new SolidColorBrush(Color.Parse("#F5A524"));
        var dir = left ? 1 : -1;
        var barX = left ? x : x - 5;
        ctx.DrawRectangle(fill, null, new RoundedRect(new Rect(barX, 0, 5, h), 2));
        // Two grip lines.
        var grip = new Pen(new SolidColorBrush(Color.Parse("#140F0A")), 1);
        ctx.DrawLine(grip, new Point(barX + 2, h * 0.35), new Point(barX + 2, h * 0.65));
        // A short lip into the kept area so the edge reads as a handle.
        ctx.DrawLine(new Pen(fill, 2), new Point(x, 1), new Point(x + dir * 6, 1));
        ctx.DrawLine(new Pen(fill, 2), new Point(x, h - 1), new Point(x + dir * 6, h - 1));
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
