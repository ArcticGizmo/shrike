using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Shrike.Core.Recording;

namespace Shrike.App.Controls;

/// <summary>
/// The zoom-authoring lane beneath the scrubber. Shares the scrubber's <b>source-time</b> x-axis so an event
/// block lines up with the video under it. Renders the authored <see cref="ZoomEvent"/>s as draggable /
/// resizable blocks, the playhead, and tick markers where clicks fired (drag edges snap to them). It edits the
/// window's event list in place and raises <see cref="Changed"/> as timing changes; focus/zoom of an event are
/// set elsewhere (creation + preview box + inspector). Pure view over the model — it never resolves viewports.
/// </summary>
public sealed class ZoomLane : Control
{
    private enum Drag { None, Move, ResizeL, ResizeR }

    private const double GripPx = 8;      // edge zone that resizes rather than moves
    private const double SnapPx = 7;      // snap distance in pixels
    private const long MinDurMs = 200;    // an event can't be shorter than this

    public Timeline? Timeline { get; set; }
    public List<ZoomEvent> Events { get; set; } = new();
    public IReadOnlyList<long> ClickMarks { get; set; } = Array.Empty<long>(); // source ms of clicks
    public int SelectedIndex { get; private set; } = -1;
    public long PlayheadMs { get; private set; }

    /// <summary>Raised when an event's timing changed (drag/resize) — the window re-resolves + persists.</summary>
    public event Action? Changed;
    /// <summary>Raised when the selected event changed (index, or -1 for none).</summary>
    public event Action<int>? SelectionChanged;
    /// <summary>Double-click on empty lane — request a new event anchored at this source ms.</summary>
    public event Action<long>? AddRequested;

    private Drag _drag = Drag.None;
    private int _dragIndex = -1;
    private long _grabOffsetMs;   // for Move: pointer-to-start offset

    public ZoomLane()
    {
        Height = 42;
        Focusable = false;
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

    public void Refresh() => InvalidateVisual();

    // ---- axis helpers ----
    private double Dur => Timeline is { DurationMs: > 0 } tl ? tl.DurationMs : 1;
    private double X(long ms) => ms / Dur * Bounds.Width;
    private long MsAt(double x) => (long)Math.Clamp(x / Math.Max(1, Bounds.Width) * Dur, 0, Dur);

    // ---- input ----
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Timeline is null || Bounds.Width <= 0) return;
        var x = e.GetPosition(this).X;

        if (e.ClickCount == 2) { if (HitTest(x) < 0) AddRequested?.Invoke(MsAt(x)); return; }

        var hit = HitTest(x);
        Select(hit);
        if (hit < 0) return;

        // Decide move vs resize from where in the block the press landed.
        var ev = Events[hit];
        double left = X(ev.StartMs), right = X(ev.EndMs);
        _dragIndex = hit;
        if (x - left <= GripPx) _drag = Drag.ResizeL;
        else if (right - x <= GripPx) _drag = Drag.ResizeR;
        else { _drag = Drag.Move; _grabOffsetMs = MsAt(x) - ev.StartMs; }
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
    }

    // Index of the event whose block contains x, or -1. Prefers the selected event when blocks overlap.
    private int HitTest(double x)
    {
        if (SelectedIndex >= 0 && SelectedIndex < Events.Count)
        {
            var s = Events[SelectedIndex];
            if (x >= X(s.StartMs) - GripPx && x <= X(s.EndMs) + GripPx) return SelectedIndex;
        }
        for (var i = 0; i < Events.Count; i++)
            if (x >= X(Events[i].StartMs) && x <= X(Events[i].EndMs)) return i;
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

        // Click markers: little amber ticks along the bottom.
        var tick = new SolidColorBrush(Color.Parse("#80F5A524"));
        foreach (var c in ClickMarks)
        {
            var cx = X(c);
            ctx.FillRectangle(tick, new Rect(cx - 0.5, h - 7, 1.5, 6));
        }

        // Event blocks.
        for (var i = 0; i < Events.Count; i++)
        {
            var ev = Events[i];
            double left = X(ev.StartMs), right = X(ev.EndMs);
            var rect = new Rect(left, 4, Math.Max(3, right - left), h - 14);
            var selected = i == SelectedIndex;
            var fill = new SolidColorBrush(Color.Parse(selected ? "#2F6E67" : "#264F4A"));
            var border = new Pen(new SolidColorBrush(Color.Parse(selected ? "#F5A524" : "#5FA9A1")), selected ? 1.8 : 1);
            ctx.DrawRectangle(fill, border, new RoundedRect(rect, 5));

            // Resize grips.
            var grip = new SolidColorBrush(Color.Parse(selected ? "#F5A524" : "#5FA9A1"));
            if (rect.Width > 2 * GripPx + 4)
            {
                ctx.FillRectangle(grip, new Rect(rect.X + 2, rect.Y + 3, 2, rect.Height - 6));
                ctx.FillRectangle(grip, new Rect(rect.Right - 4, rect.Y + 3, 2, rect.Height - 6));
            }

            // Label: the zoom factor.
            var label = ev.Zoom.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + "×";
            var ft = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 11, new SolidColorBrush(Color.Parse("#EDE5D6")));
            if (rect.Width > ft.Width + 10)
                ctx.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
        }

        // Playhead.
        var amber = new SolidColorBrush(Color.Parse("#F5A524"));
        var px = X(PlayheadMs);
        ctx.DrawLine(new Pen(amber, 1) { }, new Point(px, 0), new Point(px, h));

        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#322A1E"))), new Rect(0.5, 0.5, w - 1, h - 1));
    }
}
