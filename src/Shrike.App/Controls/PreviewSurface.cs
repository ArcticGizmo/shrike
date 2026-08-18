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

    /// <summary>A spotlight glow to preview: a soft radial halo under the cursor. Centre is normalised [0..1]
    /// within the displayed frame; radius is a fraction of the drawn frame height, matching the export.</summary>
    public readonly record struct PreviewSpotlight(Point Center, double RadiusFraction, Color Color, double Alpha);

    private IImage? _image;
    private Point? _cursor;   // optional overlay cursor, normalised [0..1] within the displayed (cropped) frame
    private Rect? _viewport;  // optional normalised source crop [0..1] — the zoom framing
    private double _cursorHeightFrac = 1.0 / 45.0; // overlay cursor height as a fraction of the drawn frame height
    private IReadOnlyList<PreviewRipple> _ripples = [];
    private PreviewSpotlight? _spotlight;

    // Zoom-aim: draw / move / resize a box on the frame to define a zoom event's focus + factor. The box is a
    // normalised square (the crop is always the frame's aspect ratio, so a square in normalised space), and
    // corner handles resize it aspect-locked with the opposite corner anchored.
    private enum AimDrag { None, New, Move, ResizeTL, ResizeTR, ResizeBL, ResizeBR }

    private const double HandlePx = 9;         // corner-handle hit/render radius
    private const double MinBoxNorm = 1.0 / 3.0; // smallest square = the 3× max zoom (1/zoom)

    private Rect? _targetBox;      // the selected event's current target, normalised [0..1]
    private Rect _fitRect;         // where the frame was last drawn (control coords) — for pointer↔normalised maths
    private AimDrag _aim = AimDrag.None;
    private Point _aimAnchor;      // normalised fixed corner (resize) or start corner (new)
    private Point _aimGrab;        // normalised offset pointer→box.TopLeft (move)

    /// <summary>When true, dragging on the preview draws / moves / resizes the target box.</summary>
    public bool AimMode { get; set; }

    /// <summary>Raised (continuously during a drag) with the normalised [0..1] target square.</summary>
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

    /// <summary>Overlay the spotlight glow active at the current time (null to clear), mirroring the export.</summary>
    public void SetSpotlight(PreviewSpotlight? spotlight)
    {
        _spotlight = spotlight;
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
        if (!AimMode || _fitRect.Width <= 0) return;
        var pos = e.GetPosition(this);
        var np = ToNorm(pos);

        _aim = AimDrag.New;
        _aimAnchor = np;
        if (_targetBox is { } b)
        {
            var corner = HitCorner(pos, b);
            if (corner != AimDrag.None) { _aim = corner; _aimAnchor = OppositeCorner(b, corner); }
            else if (ControlRectOf(b).Contains(pos)) { _aim = AimDrag.Move; _aimGrab = new Point(np.X - b.X, np.Y - b.Y); }
        }
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_aim == AimDrag.None) return;
        var np = ToNorm(e.GetPosition(this));

        Rect box;
        if (_aim == AimDrag.Move && _targetBox is { } cur)
        {
            var size = cur.Width;
            box = new Rect(Math.Clamp(np.X - _aimGrab.X, 0, 1 - size), Math.Clamp(np.Y - _aimGrab.Y, 0, 1 - size), size, size);
        }
        else
        {
            // New / resize: an aspect-locked square anchored at _aimAnchor, growing toward the pointer.
            var rawSize = Math.Max(Math.Abs(np.X - _aimAnchor.X), Math.Abs(np.Y - _aimAnchor.Y));
            // A brand-new drag shorter than the minimum is a stray click, not a box — ignore it (checked on the
            // raw distance, before the clamp below floors it to MinBoxNorm).
            if (_aim == AimDrag.New && rawSize < MinBoxNorm) return;

            var dirX = np.X >= _aimAnchor.X ? 1 : -1;
            var dirY = np.Y >= _aimAnchor.Y ? 1 : -1;
            var maxX = dirX > 0 ? 1 - _aimAnchor.X : _aimAnchor.X;
            var maxY = dirY > 0 ? 1 - _aimAnchor.Y : _aimAnchor.Y;
            var size = Math.Clamp(rawSize, MinBoxNorm, Math.Max(MinBoxNorm, Math.Min(maxX, maxY)));
            var x = dirX > 0 ? _aimAnchor.X : _aimAnchor.X - size;
            var y = dirY > 0 ? _aimAnchor.Y : _aimAnchor.Y - size;
            box = new Rect(x, y, size, size);
        }

        TargetBoxDrawn?.Invoke(box);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_aim == AimDrag.None) return;
        _aim = AimDrag.None;
        e.Pointer.Capture(null);
    }

    // ---- aim maths ----
    private Point ToNorm(Point p) => new(
        Math.Clamp((p.X - _fitRect.X) / _fitRect.Width, 0, 1),
        Math.Clamp((p.Y - _fitRect.Y) / _fitRect.Height, 0, 1));

    private Rect ControlRectOf(Rect norm) => new(
        _fitRect.X + norm.X * _fitRect.Width, _fitRect.Y + norm.Y * _fitRect.Height,
        norm.Width * _fitRect.Width, norm.Height * _fitRect.Height);

    private AimDrag HitCorner(Point p, Rect norm)
    {
        var r = ControlRectOf(norm);
        if (Near(p, r.TopLeft)) return AimDrag.ResizeTL;
        if (Near(p, r.TopRight)) return AimDrag.ResizeTR;
        if (Near(p, r.BottomLeft)) return AimDrag.ResizeBL;
        if (Near(p, r.BottomRight)) return AimDrag.ResizeBR;
        return AimDrag.None;
        static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) <= HandlePx && Math.Abs(a.Y - b.Y) <= HandlePx;
    }

    private static Point OppositeCorner(Rect b, AimDrag corner) => corner switch
    {
        AimDrag.ResizeTL => new Point(b.Right, b.Bottom),
        AimDrag.ResizeTR => new Point(b.X, b.Bottom),
        AimDrag.ResizeBL => new Point(b.Right, b.Y),
        _ => new Point(b.X, b.Y), // ResizeBR
    };

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

        // Spotlight glow sits under everything else, following the cursor (drawn as a radial gradient ellipse).
        if (_spotlight is { } sp && sp.Alpha > 0)
        {
            var centre = new Point(rect.X + sp.Center.X * rect.Width, rect.Y + sp.Center.Y * rect.Height);
            var radius = sp.RadiusFraction * rect.Height;
            if (radius > 0)
            {
                var inner = Color.FromArgb((byte)Math.Clamp(sp.Alpha * 255, 0, 255), sp.Color.R, sp.Color.G, sp.Color.B);
                var outer = Color.FromArgb(0, sp.Color.R, sp.Color.G, sp.Color.B);
                var brush = new RadialGradientBrush
                {
                    Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                    GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                    RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                    RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(inner, 0), new GradientStop(outer, 1) },
                };
                ctx.DrawEllipse(brush, null, centre, radius, radius);
            }
        }

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

        // Zoom-aim overlay: dim outside the target square, outline it, and draw aspect-locked corner handles.
        if (AimMode && _targetBox is { } tb)
        {
            var box = new Rect(rect.X + tb.X * rect.Width, rect.Y + tb.Y * rect.Height, tb.Width * rect.Width, tb.Height * rect.Height);
            var amber = Color.Parse("#F5A524");
            DimOutside(ctx, rect, box);
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(amber), 1.6), box);

            var fill = new SolidColorBrush(amber);
            var stroke = new Pen(new SolidColorBrush(Color.Parse("#140F0A")), 1);
            foreach (var corner in new[] { box.TopLeft, box.TopRight, box.BottomLeft, box.BottomRight })
                ctx.DrawRectangle(fill, stroke, new Rect(corner.X - 4, corner.Y - 4, 8, 8), 2, 2);
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
