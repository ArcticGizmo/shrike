using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Shrike.Core.Recording;

namespace Shrike.App.Controls;

/// <summary>
/// The scrubber lane of the timeline editor: a filmstrip of thumbnails across the source, with cut spans
/// dimmed, the current playhead, and optional in/out marks drawn over it. Pointer drags scrub — the
/// control raises <see cref="Scrubbing"/> continuously and <see cref="Seeked"/> on release, so the window
/// can update the preview cheaply while dragging and settle on release. Pure view: it holds a reference to
/// the <see cref="Timeline"/> for rendering but never edits it.
/// </summary>
public sealed class TimelineStrip : Control
{
    private readonly List<(long Ms, Bitmap Thumb)> _thumbs = new();
    private bool _dragging;

    public Timeline? Timeline { get; set; }
    public long PlayheadMs { get; private set; }
    public long? MarkInMs { get; set; }
    public long? MarkOutMs { get; set; }

    /// <summary>Raised repeatedly while dragging (cheap preview) with the source ms under the pointer.</summary>
    public event Action<long>? Scrubbing;
    /// <summary>Raised when the pointer is released — the committed seek position.</summary>
    public event Action<long>? Seeked;

    public TimelineStrip()
    {
        Height = 76;
        Focusable = false;
    }

    public void SetPlayhead(long ms)
    {
        PlayheadMs = ms;
        InvalidateVisual();
    }

    public void AddThumbnail(long ms, Bitmap thumb)
    {
        _thumbs.Add((ms, thumb));
        InvalidateVisual();
    }

    public void Refresh() => InvalidateVisual();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        e.Pointer.Capture(this);
        Scrub(e.GetPosition(this).X, commit: false);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) Scrub(e.GetPosition(this).X, commit: false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        Scrub(e.GetPosition(this).X, commit: true);
    }

    private void Scrub(double x, bool commit)
    {
        if (Timeline is null || Bounds.Width <= 0) return;
        var ms = (long)Math.Clamp(x / Bounds.Width * Timeline.DurationMs, 0, Timeline.DurationMs);
        SetPlayhead(ms);
        if (commit) Seeked?.Invoke(ms);
        else Scrubbing?.Invoke(ms);
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width;
        var h = Bounds.Height;
        var tl = Timeline;
        if (tl is null || w <= 0) return;

        double X(long ms) => ms / (double)tl.DurationMs * w;

        // Backdrop.
        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#0E0B06")), new Rect(0, 0, w, h));

        // Filmstrip: each thumbnail occupies the slice of the strip around its timestamp.
        if (_thumbs.Count > 0)
        {
            var slot = w / _thumbs.Count;
            for (var i = 0; i < _thumbs.Count; i++)
            {
                var dest = new Rect(i * slot, 0, slot + 1, h);
                using (ctx.PushClip(dest))
                    ctx.DrawImage(_thumbs[i].Thumb, Fit(_thumbs[i].Thumb, dest));
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

        // In/out marks.
        var markPen = new Pen(new SolidColorBrush(Color.Parse("#38BDF8")), 1.5, DashStyle.Dash);
        if (MarkInMs is { } mi) ctx.DrawLine(markPen, new Point(X(mi), 0), new Point(X(mi), h));
        if (MarkOutMs is { } mo) ctx.DrawLine(markPen, new Point(X(mo), 0), new Point(X(mo), h));

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

    // Center-crop the thumbnail to fill its slot without distortion.
    private static Rect Fit(Bitmap bmp, Rect dest)
    {
        var scale = Math.Max(dest.Width / bmp.PixelSize.Width, dest.Height / bmp.PixelSize.Height);
        var dw = bmp.PixelSize.Width * scale;
        var dh = bmp.PixelSize.Height * scale;
        return new Rect(dest.X + (dest.Width - dw) / 2, dest.Y + (dest.Height - dh) / 2, dw, dh);
    }
}
