using Shrike.Core.Annotations;

namespace Shrike.Tests;

public class AnnotationGeometryTests
{
    [Fact]
    public void HitTest_inside_rectangle_bounds()
    {
        var rect = new RectAnnotation(10, 10, 100, 50);
        Assert.True(AnnotationGeometry.HitTest(rect, new PointD(50, 30), 2));
        Assert.False(AnnotationGeometry.HitTest(rect, new PointD(200, 200), 2));
    }

    [Fact]
    public void HitTest_ellipse_rejects_corner_inside_bounds()
    {
        var ellipse = new EllipseAnnotation(0, 0, 100, 100);
        Assert.True(AnnotationGeometry.HitTest(ellipse, new PointD(50, 50), 2));   // centre
        Assert.False(AnnotationGeometry.HitTest(ellipse, new PointD(2, 2), 2));    // corner, outside the ellipse
    }

    [Fact]
    public void HitTest_line_uses_distance_to_segment()
    {
        var line = new LineAnnotation(0, 0, 100, 0) { StrokeWidth = 2 };
        Assert.True(AnnotationGeometry.HitTest(line, new PointD(50, 1), 3));
        Assert.False(AnnotationGeometry.HitTest(line, new PointD(50, 40), 3));
    }

    [Fact]
    public void HitTest_step_badge_uses_radius()
    {
        var badge = new StepBadgeAnnotation(100, 100, 1) { StrokeWidth = 4 };
        var radius = AnnotationGeometry.BadgeDiameter(4) / 2;
        Assert.True(AnnotationGeometry.HitTest(badge, new PointD(100, 100), 0));
        Assert.False(AnnotationGeometry.HitTest(badge, new PointD(100 + radius + 10, 100), 0));
    }

    [Fact]
    public void Translate_shifts_rectangle_and_preserves_style()
    {
        var rect = new RectAnnotation(10, 20, 30, 40) { Color = "#ABCDEF", StrokeWidth = 7 };
        var moved = (RectAnnotation)AnnotationGeometry.Translate(rect, 5, -3);

        Assert.Equal(15, moved.X);
        Assert.Equal(17, moved.Y);
        Assert.Equal(30, moved.Width);
        Assert.Equal("#ABCDEF", moved.Color);
        Assert.Equal(7, moved.StrokeWidth);
    }

    [Fact]
    public void Translate_shifts_line_endpoints()
    {
        var line = new LineAnnotation(0, 0, 10, 10);
        var moved = (LineAnnotation)AnnotationGeometry.Translate(line, 2, 3);
        Assert.Equal((2, 3, 12, 13), (moved.X1, moved.Y1, moved.X2, moved.Y2));
    }

    [Fact]
    public void Translate_shifts_every_freehand_point()
    {
        var fh = new FreehandAnnotation([new PointD(0, 0), new PointD(5, 5)]);
        var moved = (FreehandAnnotation)AnnotationGeometry.Translate(fh, 1, 1);
        Assert.Equal(new PointD(1, 1), moved.Points[0]);
        Assert.Equal(new PointD(6, 6), moved.Points[1]);
    }

    [Fact]
    public void Bounds_normalises_negative_extent()
    {
        // A rect authored right-to-left still yields positive-extent bounds.
        var rect = new RectAnnotation(100, 100, -40, -20);
        var b = AnnotationGeometry.Bounds(rect);
        Assert.Equal(new RectD(60, 80, 40, 20), b);
    }
}
