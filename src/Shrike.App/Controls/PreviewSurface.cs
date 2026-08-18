using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    /// <summary>A click ripple to preview: an expanding ring. Centre is normalised [0..1] within the displayed
    /// (cropped) frame; the radius/thickness are fractions of the drawn frame height so they read as true circles
    /// and stay the same on-screen size through zoom — matching how the export bakes them.</summary>
    public readonly record struct PreviewRipple(Point Center, double RadiusFraction, double ThicknessFraction, double Alpha);

    private IImage? _image;
    private Point? _cursor;   // optional overlay cursor, normalised [0..1] within the displayed (cropped) frame
    private Rect? _viewport;  // optional normalised source crop [0..1] — the zoom framing
    private double _cursorHeightFrac = 1.0 / 45.0; // overlay cursor height as a fraction of the drawn frame height
    private IReadOnlyList<PreviewRipple> _ripples = [];

    // Zoom-aim: draw a box on the frame to define a zoom event's focus + factor.
    private Rect? _targetBox;      // the selected event's current target, normalised [0..1]
    private Rect _fitRect;         // where the frame was last drawn (control coords) — for pointer↔normalised maths
    private bool _drawingBox;
    private Point _boxStart;       // control coords
    private Rect? _liveBox;        // in-progress box, control coords

    /// <summary>When true, dragging on the preview draws a target box (raising <see cref="TargetBoxDrawn"/>).</summary>
    public bool AimMode { get; set; }

    /// <summary>Raised with a normalised [0..1] rectangle when the user finishes dragging a target box.</summary>
    public event Action<Rect>? TargetBoxDrawn;

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

    /// <summary>Set the overlay cursor's height as a fraction of the drawn frame height, so the previewed cursor
    /// matches the export's resolution-scaled size (see <c>CursorStyle.ForExport</c>). Keeps preview WYSIWYG.</summary>
    public void SetCursorScale(double heightFraction)
    {
        _cursorHeightFrac = heightFraction;
        InvalidateVisual();
    }

    /// <summary>Overlay the click ripples active at the current time (empty to clear), so the previewed clicks
    /// match what the export bakes in.</summary>
    public void SetRipples(IReadOnlyList<PreviewRipple> ripples)
    {
        _ripples = ripples;
        InvalidateVisual();
    }

    /// <summary>Show the selected zoom event's target box (normalised), or clear it with null.</summary>
    public void SetTargetBox(Rect? normalized)
    {
        _targetBox = normalized;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!AimMode) return;
        _drawingBox = true;
        _boxStart = e.GetPosition(this);
        _liveBox = null;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_drawingBox) return;
        var p = e.GetPosition(this);
        _liveBox = new Rect(
            Math.Min(_boxStart.X, p.X), Math.Min(_boxStart.Y, p.Y),
            Math.Abs(p.X - _boxStart.X), Math.Abs(p.Y - _boxStart.Y));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_drawingBox) return;
        _drawingBox = false;
        e.Pointer.Capture(null);

        if (_liveBox is { } box && _fitRect.Width > 0 && _fitRect.Height > 0)
        {
            // Control coords → normalised frame coords, clamped to the frame.
            var nx = Math.Clamp((box.X - _fitRect.X) / _fitRect.Width, 0, 1);
            var ny = Math.Clamp((box.Y - _fitRect.Y) / _fitRect.Height, 0, 1);
            var nw = Math.Clamp(box.Width / _fitRect.Width, 0, 1 - nx);
            var nh = Math.Clamp(box.Height / _fitRect.Height, 0, 1 - ny);
            if (nw > 0.04 && nh > 0.04) TargetBoxDrawn?.Invoke(new Rect(nx, ny, nw, nh));
        }
        _liveBox = null;
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
        _fitRect = rect;
        ctx.DrawImage(img, srcRect, rect);

        // Ripples sit under the cursor, anchored where the click landed.
        foreach (var r in _ripples)
        {
            if (r.Alpha <= 0) continue;
            var centre = new Point(rect.X + r.Center.X * rect.Width, rect.Y + r.Center.Y * rect.Height);
            var radius = r.RadiusFraction * rect.Height;
            var thickness = Math.Max(1.0, r.ThicknessFraction * rect.Height);
            var colour = Color.FromArgb((byte)Math.Clamp(r.Alpha * 255, 0, 255), 0xF5, 0xA5, 0x24); // amber
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(colour), thickness), centre, radius, radius);
        }

        if (_cursor is { } c)
            DrawCursor(ctx, new Point(rect.X + c.X * rect.Width, rect.Y + c.Y * rect.Height), _cursorHeightFrac * rect.Height);

        // Zoom-aim overlay: the selected event's target (dashed) + any in-progress drag (solid), dimming outside.
        if (AimMode)
        {
            if (_targetBox is { } tb)
            {
                var box = new Rect(rect.X + tb.X * rect.Width, rect.Y + tb.Y * rect.Height, tb.Width * rect.Width, tb.Height * rect.Height);
                DimOutside(ctx, rect, box);
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#F5A524")), 1.6, DashStyle.Dash), box);
            }
            if (_liveBox is { } lb)
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(30, 0xF5, 0xA5, 0x24)),
                    new Pen(new SolidColorBrush(Color.Parse("#F5A524")), 1.6), lb);
        }
    }

    // Darken the frame outside the target box so the aim reads clearly.
    private static void DimOutside(DrawingContext ctx, Rect frame, Rect box)
    {
        var dim = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
        ctx.FillRectangle(dim, new Rect(frame.X, frame.Y, frame.Width, box.Y - frame.Y));                 // top
        ctx.FillRectangle(dim, new Rect(frame.X, box.Bottom, frame.Width, frame.Bottom - box.Bottom));    // bottom
        ctx.FillRectangle(dim, new Rect(frame.X, box.Y, box.X - frame.X, box.Height));                    // left
        ctx.FillRectangle(dim, new Rect(box.Right, box.Y, frame.Right - box.Right, box.Height));          // right
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

    private const double ArrowGeometryHeight = 19.4; // the BuildArrow() figure's height, in geometry units

    private static void DrawCursor(DrawingContext ctx, Point at, double heightPx)
    {
        var scale = Math.Max(0.1, heightPx / ArrowGeometryHeight);
        using (ctx.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(at.X, at.Y)))
            ctx.DrawGeometry(CursorFill, CursorPen, CursorArrow);
    }
}
