using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Shrike.App.Imaging;
using Shrike.Core.Annotations;
using Shrike.Core.Capture;
using Path = Avalonia.Controls.Shapes.Path;

namespace Shrike.App.Controls;

/// <summary>The annotation tools the editor can be in.</summary>
public enum AnnotationTool
{
    None,
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Freehand,
    Highlight,
    Redaction,
}

/// <summary>
/// The editor's drawing surface: shows a capture fitted to the control and lets the user draw
/// annotations over it (drag to create). Annotations live in a <see cref="AnnotationDocument"/> in
/// image-pixel coordinates; this control renders them as Avalonia shapes both for editing and — via
/// <see cref="RenderFlattened"/> at full resolution — for export, so what you see is what you save.
/// </summary>
public sealed class AnnotationSurface : UserControl
{
    private readonly Grid _root = new();
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly Image _baseImage = new() { Stretch = Stretch.Fill };

    private CapturedImage? _image;
    private AnnotationDocument? _document;
    private double _scale = 1;

    private bool _dragging;
    private Point _dragStart;
    private Control? _preview;
    private List<PointD> _freehand = [];

    public AnnotationTool Tool { get; set; } = AnnotationTool.None;
    public string StrokeColorHex { get; set; } = "#F5A524";
    public double StrokeWidth { get; set; } = 4;

    public AnnotationSurface()
    {
        ClipToBounds = true;
        _canvas.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        _canvas.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        _canvas.Children.Add(_baseImage);
        _root.Children.Add(_canvas);
        Content = _root;

        _baseImage.IsHitTestVisible = false;

        _canvas.PointerPressed += OnPressed;
        _canvas.PointerMoved += OnMoved;
        _canvas.PointerReleased += OnReleased;

        SizeChanged += (_, _) => Layout();
    }

    /// <summary>Load a capture + its annotation document into the surface.</summary>
    public void SetContent(CapturedImage image, AnnotationDocument document)
    {
        if (_document is not null) _document.Changed -= Rerender;
        _image = image;
        _document = document;
        _document.Changed += Rerender;
        _baseImage.Source = BitmapConverter.ToBitmap(image);
        Layout();
    }

    private void Layout()
    {
        if (_image is null) return;
        var availW = Bounds.Width;
        var availH = Bounds.Height;
        if (availW <= 0 || availH <= 0) return;

        var scale = Math.Min(availW / _image.Width, availH / _image.Height);
        _scale = scale is <= 0 or double.NaN ? 1 : Math.Min(scale, 4);

        _canvas.Width = _image.Width * _scale;
        _canvas.Height = _image.Height * _scale;
        _baseImage.Width = _canvas.Width;
        _baseImage.Height = _canvas.Height;
        Canvas.SetLeft(_baseImage, 0);
        Canvas.SetTop(_baseImage, 0);

        Rerender();
    }

    private void Rerender()
    {
        // Drop everything except the base image, then rebuild from the document at the current scale.
        for (var i = _canvas.Children.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_canvas.Children[i], _baseImage))
                _canvas.Children.RemoveAt(i);
        }

        if (_document is null) return;
        foreach (var annotation in _document.Items)
        {
            var control = BuildControl(annotation, _scale);
            if (control is not null)
            {
                control.IsHitTestVisible = false;
                _canvas.Children.Add(control);
            }
        }
    }

    // ---- drawing ----

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_document is null || Tool == AnnotationTool.None) return;

        _dragging = true;
        _dragStart = e.GetPosition(_canvas);
        _freehand = [ToImage(_dragStart)];
        e.Pointer.Capture(_canvas);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(_canvas);

        if (Tool == AnnotationTool.Freehand)
            _freehand.Add(ToImage(p));

        if (_preview is not null) _canvas.Children.Remove(_preview);
        _preview = BuildPreview(_dragStart, p);
        if (_preview is not null)
        {
            _preview.IsHitTestVisible = false;
            _canvas.Children.Add(_preview);
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging || _document is null) return;
        _dragging = false;

        if (_preview is not null) { _canvas.Children.Remove(_preview); _preview = null; }

        var end = e.GetPosition(_canvas);
        var annotation = BuildAnnotation(_dragStart, end);
        if (annotation is not null)
            _document.Add(annotation); // Changed → Rerender paints the committed shape
    }

    private PointD ToImage(Point canvasPoint) => new(canvasPoint.X / _scale, canvasPoint.Y / _scale);

    /// <summary>Build the annotation (image coords) for the finished drag, or null if too small.</summary>
    private Annotation? BuildAnnotation(Point start, Point end)
    {
        var a = ToImage(start);
        var b = ToImage(end);
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var w = Math.Abs(b.X - a.X);
        var h = Math.Abs(b.Y - a.Y);

        switch (Tool)
        {
            case AnnotationTool.Rectangle:
                return w < 3 || h < 3 ? null : Style(new RectAnnotation(x, y, w, h));
            case AnnotationTool.Ellipse:
                return w < 3 || h < 3 ? null : Style(new EllipseAnnotation(x, y, w, h));
            case AnnotationTool.Highlight:
                return w < 3 || h < 3 ? null : Style(new HighlightAnnotation(x, y, w, h));
            case AnnotationTool.Redaction:
                return w < 3 || h < 3 ? null : Style(new RedactionAnnotation(x, y, w, h));
            case AnnotationTool.Line:
                return Distance(a, b) < 3 ? null : Style(new LineAnnotation(a.X, a.Y, b.X, b.Y));
            case AnnotationTool.Arrow:
                return Distance(a, b) < 3 ? null : Style(new LineAnnotation(a.X, a.Y, b.X, b.Y, Arrow: true));
            case AnnotationTool.Freehand:
                return _freehand.Count < 2 ? null : Style(new FreehandAnnotation(_freehand));
            default:
                return null;
        }
    }

    private Annotation Style(Annotation a) => a with { Color = StrokeColorHex, StrokeWidth = StrokeWidth };

    private static double Distance(PointD a, PointD b) => Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));

    /// <summary>Live preview shape during a drag (canvas coordinates).</summary>
    private Control? BuildPreview(Point start, Point current)
    {
        var brush = ParseBrush(StrokeColorHex);
        var thickness = StrokeWidth * _scale;
        var x = Math.Min(start.X, current.X);
        var y = Math.Min(start.Y, current.Y);
        var w = Math.Abs(current.X - start.X);
        var h = Math.Abs(current.Y - start.Y);

        return Tool switch
        {
            AnnotationTool.Rectangle => PlaceBox(new Rectangle { Stroke = brush, StrokeThickness = thickness }, x, y, w, h),
            AnnotationTool.Ellipse => PlaceBox(new Ellipse { Stroke = brush, StrokeThickness = thickness }, x, y, w, h),
            AnnotationTool.Highlight => PlaceBox(new Rectangle { Fill = HighlightBrush(StrokeColorHex) }, x, y, w, h),
            AnnotationTool.Redaction => PlaceBox(new Rectangle { Fill = Brushes.Black }, x, y, w, h),
            AnnotationTool.Line => new Path { Stroke = brush, StrokeThickness = thickness, Data = LineGeometry(start, current, false) },
            AnnotationTool.Arrow => new Path { Stroke = brush, StrokeThickness = thickness, Data = LineGeometry(start, current, true) },
            AnnotationTool.Freehand => FreehandPreview(brush, thickness),
            _ => null,
        };
    }

    private Control FreehandPreview(IBrush brush, double thickness)
    {
        var poly = new Polyline { Stroke = brush, StrokeThickness = thickness };
        foreach (var pt in _freehand) poly.Points.Add(new Point(pt.X * _scale, pt.Y * _scale));
        return poly;
    }

    /// <summary>Build a committed annotation as an Avalonia control at the given scale (1.0 = export).</summary>
    private Control? BuildControl(Annotation annotation, double scale)
    {
        var thickness = annotation.StrokeWidth * scale;
        switch (annotation)
        {
            case RectAnnotation r:
                return PlaceBox(new Rectangle { Stroke = ParseBrush(r.Color), StrokeThickness = thickness },
                    r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale);
            case EllipseAnnotation el:
                return PlaceBox(new Ellipse { Stroke = ParseBrush(el.Color), StrokeThickness = thickness },
                    el.X * scale, el.Y * scale, el.Width * scale, el.Height * scale);
            case HighlightAnnotation hl:
                return PlaceBox(new Rectangle { Fill = HighlightBrush(hl.Color) },
                    hl.X * scale, hl.Y * scale, hl.Width * scale, hl.Height * scale);
            case RedactionAnnotation rd:
                return PlaceBox(new Rectangle { Fill = Brushes.Black },
                    rd.X * scale, rd.Y * scale, rd.Width * scale, rd.Height * scale);
            case LineAnnotation ln:
                return new Path
                {
                    Stroke = ParseBrush(ln.Color),
                    StrokeThickness = thickness,
                    Data = LineGeometry(new Point(ln.X1 * scale, ln.Y1 * scale), new Point(ln.X2 * scale, ln.Y2 * scale), ln.Arrow),
                };
            case FreehandAnnotation fh:
                var poly = new Polyline { Stroke = ParseBrush(fh.Color), StrokeThickness = thickness };
                foreach (var pt in fh.Points) poly.Points.Add(new Point(pt.X * scale, pt.Y * scale));
                return poly;
            default:
                return null; // Text / StepBadge land in a later step
        }
    }

    private static Control PlaceBox(Control control, double x, double y, double w, double h)
    {
        control.Width = w;
        control.Height = h;
        Canvas.SetLeft(control, x);
        Canvas.SetTop(control, y);
        return control;
    }

    private static Geometry LineGeometry(Point p1, Point p2, bool arrow)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(p1, false);
        ctx.LineTo(p2);
        ctx.EndFigure(false);

        if (arrow)
        {
            var head = Math.Max(10, 4 * Math.Sqrt(Math.Pow(p2.X - p1.X, 2) + Math.Pow(p2.Y - p1.Y, 2)) / 20);
            head = Math.Min(head, 26);
            var angle = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
            var left = angle + Math.PI - 0.45;
            var right = angle + Math.PI + 0.45;
            var h1 = new Point(p2.X + head * Math.Cos(left), p2.Y + head * Math.Sin(left));
            var h2 = new Point(p2.X + head * Math.Cos(right), p2.Y + head * Math.Sin(right));
            ctx.BeginFigure(h1, false);
            ctx.LineTo(p2);
            ctx.LineTo(h2);
            ctx.EndFigure(false);
        }

        return geometry;
    }

    private static IBrush ParseBrush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static IBrush HighlightBrush(string hex)
    {
        var c = Color.Parse(hex);
        return new SolidColorBrush(Color.FromArgb(0x66, c.R, c.G, c.B));
    }

    /// <summary>
    /// Render the capture + annotations to a full-resolution image for export. Redaction rects are
    /// drawn opaque here and additionally scrubbed by <see cref="Redaction"/> at export for the
    /// destruction guarantee.
    /// </summary>
    public CapturedImage? RenderFlattened()
    {
        if (_image is null || _document is null) return null;

        var w = _image.Width;
        var h = _image.Height;

        var export = new Canvas { Width = w, Height = h, Background = Brushes.Transparent };
        var baseImg = new Image { Source = BitmapConverter.ToBitmap(_image), Stretch = Stretch.Fill, Width = w, Height = h };
        Canvas.SetLeft(baseImg, 0);
        Canvas.SetTop(baseImg, 0);
        export.Children.Add(baseImg);

        foreach (var annotation in _document.Items)
        {
            var control = BuildControl(annotation, 1.0);
            if (control is not null) export.Children.Add(control);
        }

        export.Measure(new Size(w, h));
        export.Arrange(new Rect(0, 0, w, h));

        var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        rtb.Render(export);

        var buffer = new byte[w * h * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            rtb.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buffer.Length, w * 4);
        }
        finally
        {
            handle.Free();
        }

        return new CapturedImage(w, h, buffer, _image.Source, _image.CapturedAt);
    }
}
