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

    [Fact]
    public void Center_is_the_bounding_box_centre()
    {
        Assert.Equal(new PointD(25, 40), AnnotationGeometry.Center(new RectAnnotation(10, 20, 30, 40)));
    }

    [Fact]
    public void Resize_right_edge_keeps_the_left_edge_fixed()
    {
        var rect = new RectAnnotation(10, 20, 100, 50);
        var r = (RectAnnotation)AnnotationGeometry.Resize(rect, ResizeGrip.Right, 10, 0);
        Assert.Equal((10.0, 20.0, 110.0, 50.0), (r.X, r.Y, r.Width, r.Height));
    }

    [Fact]
    public void Resize_top_left_corner_keeps_the_opposite_corner_fixed()
    {
        var rect = new RectAnnotation(0, 0, 100, 100);
        var r = (RectAnnotation)AnnotationGeometry.Resize(rect, ResizeGrip.TopLeft, 10, 20);
        // Dragging the top-left in by (10,20): the bottom-right corner (100,100) must not move.
        Assert.Equal((10.0, 20.0, 90.0, 80.0), (r.X, r.Y, r.Width, r.Height));
        Assert.Equal((100.0, 100.0), (r.X + r.Width, r.Y + r.Height));
    }

    [Fact]
    public void Resize_applies_a_minimum_size_floor()
    {
        var rect = new RectAnnotation(0, 0, 100, 100);
        var r = (RectAnnotation)AnnotationGeometry.Resize(rect, ResizeGrip.Right, -200, 0);
        Assert.Equal(4.0, r.Width); // collapsed to the floor, not inverted
    }

    [Fact]
    public void Resize_rotated_rectangle_maps_the_drag_into_its_own_frame()
    {
        // A 90°-rotated square, dragging the (local) right edge with a world dy of 10.
        var rect = new RectAnnotation(0, 0, 100, 100) { Rotation = 90 };
        var r = (RectAnnotation)AnnotationGeometry.Resize(rect, ResizeGrip.Right, 0, 10);
        Assert.Equal(-5.0, r.X, 6);
        Assert.Equal(5.0, r.Y, 6);
        Assert.Equal(110.0, r.Width, 6);
        Assert.Equal(100.0, r.Height, 6);
        Assert.Equal(90.0, r.Rotation, 6);
    }

    [Fact]
    public void ScaleText_grows_the_font_and_pins_the_opposite_corner()
    {
        // Bounds height = FontSize*1.3; dragging the bottom-right along the diagonal by one full
        // diagonal doubles the font, with the top-left corner fixed.
        var text = new TextAnnotation(10, 10, "Hi", 20);
        var b = AnnotationGeometry.Bounds(text);
        var scaled = (TextAnnotation)AnnotationGeometry.ScaleText(text, ResizeGrip.BottomRight, b.Width, b.Height);
        Assert.Equal(40, scaled.FontSize, 6);
        Assert.Equal(10, scaled.X, 6); // top-left anchor unchanged
        Assert.Equal(10, scaled.Y, 6);
    }

    [Fact]
    public void ScaleText_clamps_to_a_minimum_font()
    {
        var text = new TextAnnotation(0, 0, "Hi", 20);
        var scaled = (TextAnnotation)AnnotationGeometry.ScaleText(text, ResizeGrip.BottomRight, -10000, -10000);
        Assert.Equal(6, scaled.FontSize, 6);
    }

    [Fact]
    public void MoveLineEndpoint_moves_only_the_grabbed_end()
    {
        var line = new LineAnnotation(0, 0, 10, 10);
        var start = AnnotationGeometry.MoveLineEndpoint(line, moveStart: true, 2, 3);
        Assert.Equal((2.0, 3.0, 10.0, 10.0), (start.X1, start.Y1, start.X2, start.Y2));
        var end = AnnotationGeometry.MoveLineEndpoint(line, moveStart: false, 2, 3);
        Assert.Equal((0.0, 0.0, 12.0, 13.0), (end.X1, end.Y1, end.X2, end.Y2));
    }

    [Fact]
    public void HitTest_respects_rotation()
    {
        // A tall-thin rect turned 90° becomes wide-flat about its centre (50,50).
        var rect = new RectAnnotation(40, 0, 20, 100) { Rotation = 90 };
        // On the now-horizontal long axis: inside the rotated shape, outside its axis-aligned bounds.
        Assert.True(AnnotationGeometry.HitTest(rect, new PointD(90, 50), 2));
        // Inside the axis-aligned bounds but off the rotated shape.
        Assert.False(AnnotationGeometry.HitTest(rect, new PointD(50, 90), 2));
    }

    [Fact]
    public void Rotate_sets_rotation_and_preserves_everything_else()
    {
        var rect = new RectAnnotation(10, 20, 30, 40) { Color = "#ABCDEF", StrokeWidth = 7 };
        var r = (RectAnnotation)AnnotationGeometry.Rotate(rect, 45);
        Assert.Equal(45, r.Rotation);
        Assert.Equal((10.0, 20.0, 30.0, 40.0), (r.X, r.Y, r.Width, r.Height));
        Assert.Equal("#ABCDEF", r.Color);
        Assert.Equal(7, r.StrokeWidth);
    }
}
