namespace Shrike.Core.Annotations;

/// <summary>Which edge/corner of a box shape a resize is driving.</summary>
public enum ResizeGrip { Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// Pure geometry for annotations — bounds, hit-testing and translation — kept UI-free so it's
/// headless-testable and shared between the editor's rendering and its select/move tooling. All
/// coordinates are image pixels. Text and badge sizes are estimates (the true text metrics live in
/// the UI), which is fine for selection: they only need to be close enough to click.
/// </summary>
public static class AnnotationGeometry
{
    /// <summary>Diameter (image px) of a step badge for a stroke width. Shared by render + hit-test.</summary>
    public static double BadgeDiameter(double strokeWidth) => 20 + strokeWidth * 2;

    /// <summary>Axis-aligned bounding box in image pixels.</summary>
    public static RectD Bounds(Annotation a) => a switch
    {
        RectAnnotation r => Norm(r.X, r.Y, r.Width, r.Height),
        EllipseAnnotation e => Norm(e.X, e.Y, e.Width, e.Height),
        HighlightAnnotation h => Norm(h.X, h.Y, h.Width, h.Height),
        RedactionAnnotation d => Norm(d.X, d.Y, d.Width, d.Height),
        LineAnnotation l => new RectD(Math.Min(l.X1, l.X2), Math.Min(l.Y1, l.Y2),
            Math.Abs(l.X2 - l.X1), Math.Abs(l.Y2 - l.Y1)),
        FreehandAnnotation f => PointsBounds(f.Points),
        TextAnnotation t => new RectD(t.X, t.Y, EstimateTextWidth(t.Text, t.FontSize), t.FontSize * 1.3),
        StepBadgeAnnotation s => BadgeBounds(s),
        _ => new RectD(0, 0, 0, 0),
    };

    /// <summary>Centre of the unrotated bounding box, in image pixels (the rotation pivot).</summary>
    public static PointD Center(Annotation a)
    {
        var b = Bounds(a);
        return new PointD(b.X + b.Width / 2, b.Y + b.Height / 2);
    }

    /// <summary>True if <paramref name="p"/> selects <paramref name="a"/> within a pixel tolerance.</summary>
    public static bool HitTest(Annotation a, PointD p, double tolerance)
    {
        // Rotated shapes hit-test in their own upright frame: undo the rotation about the centre, then
        // run the ordinary axis-aligned tests. Rotation == 0 is a no-op, so unrotated behaviour is exact.
        if (a.Rotation != 0)
            p = RotateAbout(p, Center(a), -a.Rotation);

        var tol = Math.Max(tolerance, a.StrokeWidth / 2);
        switch (a)
        {
            case LineAnnotation l:
                return DistanceToSegment(p, new PointD(l.X1, l.Y1), new PointD(l.X2, l.Y2)) <= tol + 2;
            case FreehandAnnotation f:
                for (var i = 1; i < f.Points.Count; i++)
                    if (DistanceToSegment(p, f.Points[i - 1], f.Points[i]) <= tol + 2) return true;
                return false;
            case EllipseAnnotation e:
                return InEllipse(p, Norm(e.X, e.Y, e.Width, e.Height), tol);
            case StepBadgeAnnotation s:
                return Distance(p, new PointD(s.X, s.Y)) <= BadgeDiameter(s.StrokeWidth) / 2 + tol;
            default:
                return Bounds(a).Inflate(tol).Contains(p);
        }
    }

    /// <summary>A copy of <paramref name="a"/> shifted by (dx, dy) image pixels; style is preserved.</summary>
    public static Annotation Translate(Annotation a, double dx, double dy) => a switch
    {
        RectAnnotation r => r with { X = r.X + dx, Y = r.Y + dy },
        EllipseAnnotation e => e with { X = e.X + dx, Y = e.Y + dy },
        HighlightAnnotation h => h with { X = h.X + dx, Y = h.Y + dy },
        RedactionAnnotation d => d with { X = d.X + dx, Y = d.Y + dy },
        LineAnnotation l => l with { X1 = l.X1 + dx, Y1 = l.Y1 + dy, X2 = l.X2 + dx, Y2 = l.Y2 + dy },
        FreehandAnnotation f => f with { Points = [.. f.Points.Select(pt => new PointD(pt.X + dx, pt.Y + dy))] },
        TextAnnotation t => t with { X = t.X + dx, Y = t.Y + dy },
        StepBadgeAnnotation s => s with { X = s.X + dx, Y = s.Y + dy },
        _ => a,
    };

    /// <summary>
    /// Resize a box shape (rectangle/ellipse/highlight/redaction) by dragging one of its edges/corners
    /// by a world-space delta, keeping the opposite edge/corner fixed. Works at any rotation: the delta
    /// is mapped into the shape's local frame, the grabbed edges move there, and the new centre is mapped
    /// back out — so at 0° it matches the crop tool exactly. Non-box shapes are returned unchanged.
    /// </summary>
    public static Annotation Resize(Annotation a, ResizeGrip grip, double dx, double dy)
    {
        if (a is not (RectAnnotation or EllipseAnnotation or HighlightAnnotation or RedactionAnnotation))
            return a;

        const double min = 4; // smallest half-sensible extent, in image px

        var b = Bounds(a);
        var cx = b.X + b.Width / 2;
        var cy = b.Y + b.Height / 2;
        var hx = b.Width / 2;
        var hy = b.Height / 2;

        var rad = a.Rotation * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);

        // World delta → local delta (local axes are ex=(cos,sin), ey=(-sin,cos)).
        var dlx = dx * cos + dy * sin;
        var dly = -dx * sin + dy * cos;

        // Local rect edges about the centre; move only the grabbed ones, with a min-size floor.
        double left = -hx, right = hx, top = -hy, bottom = hy;
        var isLeft = grip is ResizeGrip.Left or ResizeGrip.TopLeft or ResizeGrip.BottomLeft;
        var isRight = grip is ResizeGrip.Right or ResizeGrip.TopRight or ResizeGrip.BottomRight;
        var isTop = grip is ResizeGrip.Top or ResizeGrip.TopLeft or ResizeGrip.TopRight;
        var isBottom = grip is ResizeGrip.Bottom or ResizeGrip.BottomLeft or ResizeGrip.BottomRight;

        if (isLeft) left = Math.Min(left + dlx, right - min);
        if (isRight) right = Math.Max(right + dlx, left + min);
        if (isTop) top = Math.Min(top + dly, bottom - min);
        if (isBottom) bottom = Math.Max(bottom + dly, top + min);

        var newHx = (right - left) / 2;
        var newHy = (bottom - top) / 2;

        // New centre = old centre + the new local-rect midpoint mapped back to world.
        var ocx = (left + right) / 2;
        var ocy = (top + bottom) / 2;
        var ncx = cx + (cos * ocx - sin * ocy);
        var ncy = cy + (sin * ocx + cos * ocy);

        var nx = ncx - newHx;
        var ny = ncy - newHy;
        var nw = newHx * 2;
        var nh = newHy * 2;

        return a switch
        {
            RectAnnotation r => r with { X = nx, Y = ny, Width = nw, Height = nh },
            EllipseAnnotation e => e with { X = nx, Y = ny, Width = nw, Height = nh },
            HighlightAnnotation h => h with { X = nx, Y = ny, Width = nw, Height = nh },
            RedactionAnnotation d => d with { X = nx, Y = ny, Width = nw, Height = nh },
            _ => a,
        };
    }

    /// <summary>Move one end of a line by a world-space delta, leaving the other end put.</summary>
    public static LineAnnotation MoveLineEndpoint(LineAnnotation l, bool moveStart, double dx, double dy)
        => moveStart
            ? l with { X1 = l.X1 + dx, Y1 = l.Y1 + dy }
            : l with { X2 = l.X2 + dx, Y2 = l.Y2 + dy };

    /// <summary>Set the clockwise rotation (degrees) of a shape about its centre; style/geometry unchanged.</summary>
    public static Annotation Rotate(Annotation a, double degrees) => a with { Rotation = degrees };

    /// <summary>Rotate <paramref name="p"/> clockwise (degrees) about <paramref name="c"/>.</summary>
    private static PointD RotateAbout(PointD p, PointD c, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var dx = p.X - c.X;
        var dy = p.Y - c.Y;
        return new PointD(c.X + dx * cos - dy * sin, c.Y + dx * sin + dy * cos);
    }

    private static RectD BadgeBounds(StepBadgeAnnotation s)
    {
        var d = BadgeDiameter(s.StrokeWidth);
        return new RectD(s.X - d / 2, s.Y - d / 2, d, d);
    }

    private static double EstimateTextWidth(string text, double fontSize)
        => Math.Max(fontSize, text.Length * fontSize * 0.6);

    private static RectD PointsBounds(IReadOnlyList<PointD> pts)
    {
        if (pts.Count == 0) return new RectD(0, 0, 0, 0);
        double minX = pts[0].X, minY = pts[0].Y, maxX = pts[0].X, maxY = pts[0].Y;
        foreach (var p in pts)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }
        return new RectD(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Normalise a possibly-negative w/h rect to a positive-extent one.</summary>
    private static RectD Norm(double x, double y, double w, double h)
        => new(w < 0 ? x + w : x, h < 0 ? y + h : y, Math.Abs(w), Math.Abs(h));

    private static bool InEllipse(PointD p, RectD b, double tol)
    {
        var rx = b.Width / 2 + tol;
        var ry = b.Height / 2 + tol;
        if (rx <= 0 || ry <= 0) return false;
        var nx = (p.X - (b.X + b.Width / 2)) / rx;
        var ny = (p.Y - (b.Y + b.Height / 2)) / ry;
        return nx * nx + ny * ny <= 1;
    }

    private static double Distance(PointD a, PointD b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double DistanceToSegment(PointD p, PointD a, PointD b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lenSq = dx * dx + dy * dy;
        if (lenSq <= 0) return Distance(p, a);
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0, 1);
        return Distance(p, new PointD(a.X + t * dx, a.Y + t * dy));
    }
}
