using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Shrike.App.Controls;

/// <summary>
/// Draws the current preview frame, Uniform-fit and centred. Used for both the crisp still shown while
/// paused/scrubbing and the live frames during playback. Unlike an <see cref="Image"/>, it repaints
/// whenever <see cref="Show"/> is called even if the bitmap reference is unchanged — which is exactly
/// what streaming playback needs, since it updates the pixels of one reused WriteableBitmap each frame.
/// </summary>
public sealed class PreviewSurface : Control
{
    private IImage? _image;
    private Point? _cursor;   // optional overlay cursor, normalised [0..1] within the displayed (cropped) frame
    private Rect? _viewport;  // optional normalised source crop [0..1] — the zoom framing

    /// <summary>Set (or refresh) the frame to display and repaint immediately.</summary>
    public void Show(IImage image)
    {
        _image = image;
        InvalidateVisual();
    }

    /// <summary>Show only a sub-rectangle of the frame (normalised 0..1), scaled to fill — the zoom preview.
    /// Null shows the whole frame.</summary>
    public void SetViewport(Rect? normalized)
    {
        _viewport = normalized;
        InvalidateVisual();
    }

    /// <summary>
    /// Overlay a synthetic cursor at a normalised position (0..1 across the frame), or clear it with null.
    /// Given in frame-relative coordinates so it scales with the Uniform-fit rect regardless of the frame's
    /// pixel resolution (playback frames are downscaled). Used to preview the smoothed cursor.
    /// </summary>
    public void SetCursor(Point? normalized)
    {
        _cursor = normalized;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var img = _image;
        if (img is null) return;

        var src = img.Size;
        if (src.Width <= 0 || src.Height <= 0) return;

        // Source sub-rectangle (the zoom crop), in image pixels; whole frame when no viewport is set.
        var srcRect = _viewport is { } v
            ? new Rect(v.X * src.Width, v.Y * src.Height, v.Width * src.Width, v.Height * src.Height)
            : new Rect(src);

        var dst = Bounds.Size;
        var scale = Math.Min(dst.Width / srcRect.Width, dst.Height / srcRect.Height);
        var w = srcRect.Width * scale;
        var h = srcRect.Height * scale;
        var rect = new Rect((dst.Width - w) / 2, (dst.Height - h) / 2, w, h);
        ctx.DrawImage(img, srcRect, rect);

        if (_cursor is { } c)
            DrawCursor(ctx, new Point(rect.X + c.X * rect.Width, rect.Y + c.Y * rect.Height));
    }

    private static readonly IBrush CursorFill = new SolidColorBrush(Color.FromRgb(0xFB, 0xF6, 0xEC));
    private static readonly IPen CursorPen = new Pen(new SolidColorBrush(Color.FromRgb(0x14, 0x11, 0x0D)), 1.4);
    private static readonly Geometry CursorArrow = BuildArrow();

    // A small arrow whose tip (hotspot) sits at (0,0), so translating to the point lands the tip there.
    private static Geometry BuildArrow()
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(new Point(0, 0), isFilled: true);
        c.LineTo(new Point(0, 17));
        c.LineTo(new Point(4.4, 12.6));
        c.LineTo(new Point(7.4, 19.4));
        c.LineTo(new Point(10.2, 18.1));
        c.LineTo(new Point(7.2, 11.4));
        c.LineTo(new Point(13, 11));
        c.EndFigure(isClosed: true);
        return g;
    }

    private static void DrawCursor(DrawingContext ctx, Point at)
    {
        using (ctx.PushTransform(Matrix.CreateTranslation(at.X, at.Y)))
            ctx.DrawGeometry(CursorFill, CursorPen, CursorArrow);
    }
}
