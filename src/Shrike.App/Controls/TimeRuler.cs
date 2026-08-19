using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Shrike.Core.Recording;

namespace Shrike.App.Controls;

/// <summary>
/// A thin time axis above the scrubber, sharing the same source-time x-axis (0..<see cref="Timeline"/>.DurationMs).
/// Ticks land on a "nice" round interval — 0.1s, 0.5s, 1s, 5s, 10s, 30s, 1m, … — picked so the labels stay
/// readable (≥ a minimum pixel spacing) for the clip's length and the control's current width. It also carries
/// the <b>playhead</b> and is draggable to scrub (raising <see cref="Scrubbing"/> as you drag and
/// <see cref="Seeked"/> on release, exactly like the filmstrip), so you can grab the time position right here.
/// </summary>
public sealed class TimeRuler : Control
{
    // Round intervals to snap ticks to (ms). Ascending; the smallest one that stays readable wins.
    private static readonly long[] Steps =
        [100, 500, 1000, 5000, 10000, 30000, 60000, 300000, 600000, 1_800_000, 3_600_000];

    private const double MinLabelPx = 64;

    public Timeline? Timeline { get; set; }
    public long PlayheadMs { get; private set; }

    /// <summary>The visible time window (source ms). When End &lt;= Start the whole clip is shown.</summary>
    public long ViewStartMs { get; private set; }
    public long ViewEndMs { get; private set; }

    /// <summary>Raised continuously while dragging the ruler — the window moves the preview to this source ms.</summary>
    public event Action<long>? Scrubbing;
    /// <summary>Raised when the drag ends (or a single click lands) — the final seek target in source ms.</summary>
    public event Action<long>? Seeked;
    /// <summary>Ctrl+wheel over the timeline: zoom the view around this source ms (delta &gt; 0 = zoom in).</summary>
    public event Action<long, double>? ZoomRequested;
    /// <summary>Shift+wheel over the timeline: pan the view by this wheel delta.</summary>
    public event Action<double>? PanRequested;

    private bool _dragging;

    public void SetView(long startMs, long endMs) { ViewStartMs = startMs; ViewEndMs = endMs; InvalidateVisual(); }

    public TimeRuler()
    {
        Height = 20;
        Focusable = false;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public void SetPlayhead(long sourceMs) { PlayheadMs = sourceMs; InvalidateVisual(); }

    private double Dur => Timeline is { DurationMs: > 0 } tl ? tl.DurationMs : 1;
    private double ViewSpan => ViewEndMs > ViewStartMs ? ViewEndMs - ViewStartMs : Dur;
    private double X(long ms) => (ms - ViewStartMs) / ViewSpan * Bounds.Width;
    private long MsAt(double x) => (long)Math.Clamp(ViewStartMs + x / Math.Max(1, Bounds.Width) * ViewSpan, 0, Dur);

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
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        Scrubbing?.Invoke(MsAt(e.GetPosition(this).X));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        Scrubbing?.Invoke(MsAt(e.GetPosition(this).X));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        Seeked?.Invoke(MsAt(e.GetPosition(this).X));
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (Timeline is not { DurationMs: > 0 } tl || w <= 0) return;
        var span = ViewSpan;
        long viewStart = ViewStartMs, viewEnd = ViewEndMs > ViewStartMs ? ViewEndMs : tl.DurationMs;

        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#120E08")), new Rect(0, 0, w, h));

        // Smallest round interval whose on-screen spacing clears the minimum — the most ticks that still read
        // across the VISIBLE span (so zooming in reveals finer ticks).
        var step = Steps[^1];
        foreach (var s in Steps)
            if (s / span * w >= MinLabelPx) { step = s; break; }

        var tickPen = new Pen(new SolidColorBrush(Color.Parse("#4A3E2C")));
        var textBrush = new SolidColorBrush(Color.Parse("#8B7E68"));
        for (long t = (long)(Math.Floor(viewStart / (double)step) * step); t <= viewEnd; t += step)
        {
            if (t < 0) continue;
            var x = X(t);
            if (x < -1 || x > w + 1) continue;
            ctx.DrawLine(tickPen, new Point(x, h - 6), new Point(x, h));
            var ft = new FormattedText(Label(t, step), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Consolas"), 10.5, textBrush);
            ctx.DrawText(ft, new Point(Math.Min(x + 3, w - ft.Width - 1), 1));
        }

        ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#322A1E"))), new Point(0, h - 0.5), new Point(w, h - 0.5));

        // Playhead: an amber line with a downward-pointing tab at the top, matching the scrubber's.
        var amber = new SolidColorBrush(Color.Parse("#F5A524"));
        var px = X(PlayheadMs);
        ctx.DrawLine(new Pen(amber, 1), new Point(px, 0), new Point(px, h));
        var tab = new StreamGeometry();
        using (var g = tab.Open())
        {
            g.BeginFigure(new Point(px - 4, 0), true);
            g.LineTo(new Point(px + 4, 0));
            g.LineTo(new Point(px, 6));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(amber, null, tab);
    }

    // Absolute time at a tick, formatted to match the chosen interval's granularity.
    private static string Label(long ms, long step)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (ms == 0) return "0";
        if (step >= 60000) return (ms / 60000) + "m";
        if (ms >= 60000) return $"{ms / 60000}:{(ms % 60000) / 1000:00}";
        if (step < 1000) return (ms / 1000.0).ToString("0.0", inv) + "s";
        return (ms / 1000) + "s";
    }
}
