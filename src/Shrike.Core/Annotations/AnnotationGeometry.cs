namespace Shrike.Core.Annotations;

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

    /// <summary>True if <paramref name="p"/> selects <paramref name="a"/> within a pixel tolerance.</summary>
    public static bool HitTest(Annotation a, PointD p, double tolerance)
    {
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
