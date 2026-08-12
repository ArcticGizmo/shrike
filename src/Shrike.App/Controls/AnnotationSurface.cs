using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
    Text,
    StepBadge,
}

/// <summary>
/// The editor's drawing surface: shows a capture fitted to the control and lets the user draw
/// annotations over it (drag to create). Annotations live in a <see cref="AnnotationDocument"/> in
/// image-pixel coordinates; this control renders them as Avalonia shapes both for editing and — via
/// <see cref="RenderFlattened"/> at full resolution — for export, so what you see is what you save.
/// </summary>
public sealed class AnnotationSurface : UserControl
{
    private readonly ScrollViewer _scroller = new();
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly Image _baseImage = new() { Stretch = Stretch.Fill };

    private CapturedImage? _image;
    private AnnotationDocument? _document;

    /// <summary>Scale that fits the whole capture inside the viewport (recomputed on resize).</summary>
    private double _fitScale = 1;

    /// <summary>Explicit zoom scale, or <c>null</c> for auto-fit mode.</summary>
    private double? _zoom;

    /// <summary>Zoom bounds (image-px : DIP). Fit can go below <see cref="MinZoom"/> for huge captures.</summary>
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8;
    private const double ZoomStep = 1.25;

    private bool _dragging;
    private Point _dragStart;
    private Control? _preview;
    private List<PointD> _freehand = [];

    /// <summary>The in-place text editor while the Text tool is placing a label (null otherwise).</summary>
    private TextBox? _textEditor;
    private PointD _textOrigin;

    /// <summary>Font size for new text labels, in image pixels.</summary>
    private const double TextFontSize = 24;

    private AnnotationTool _tool = AnnotationTool.None;

    /// <summary>Active tool. Switching tools commits any text edit in progress.</summary>
    public AnnotationTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            CommitTextEdit();
            _tool = value;
        }
    }

    public string StrokeColorHex { get; set; } = "#F5A524";
    public double StrokeWidth { get; set; } = 4;

    /// <summary>True while an in-place text label is being typed (so the editor can pause shortcuts).</summary>
    public bool IsEditingText => _textEditor is not null;

    /// <summary>The scale actually in effect (explicit zoom if set, otherwise the fit scale).</summary>
    private double Scale => _zoom ?? _fitScale;

    /// <summary>True while the surface auto-fits the capture to the viewport.</summary>
    public bool IsFit => _zoom is null;

    /// <summary>Current zoom as a percentage (100% = one image pixel per DIP).</summary>
    public double ZoomPercent => Scale * 100;

    /// <summary>Raised whenever the effective zoom changes, so the editor can update its label.</summary>
    public event Action? ZoomChanged;

    public AnnotationSurface()
    {
        ClipToBounds = true;

        _canvas.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        _canvas.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        _canvas.Children.Add(_baseImage);

        _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroller.Content = _canvas;
        Content = _scroller;

        _baseImage.IsHitTestVisible = false;

        _canvas.PointerPressed += OnPressed;
        _canvas.PointerMoved += OnMoved;
        _canvas.PointerReleased += OnReleased;
        PointerWheelChanged += OnWheel;

        SizeChanged += (_, _) => Layout();
    }

    /// <summary>Load a capture + its annotation document into the surface.</summary>
    public void SetContent(CapturedImage image, AnnotationDocument document)
    {
        CancelTextEdit();
        if (_document is not null) _document.Changed -= Rerender;
        _image = image;
        _document = document;
        _document.Changed += Rerender;
        _baseImage.Source = BitmapConverter.ToBitmap(image);
        _zoom = null; // every new capture opens fit-to-window
        Layout();
        ZoomChanged?.Invoke();
    }

    private void Layout()
    {
        if (_image is null) return;
        var availW = Bounds.Width;
        var availH = Bounds.Height;
        if (availW <= 0 || availH <= 0) return;

        var fit = Math.Min(availW / _image.Width, availH / _image.Height);
        _fitScale = fit is <= 0 or double.NaN ? 1 : Math.Min(fit, 4);

        var scale = Scale;
        _canvas.Width = _image.Width * scale;
        _canvas.Height = _image.Height * scale;
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
            var control = BuildControl(annotation, Scale);
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

        // A text edit in progress commits on the next click elsewhere (like most editors).
        if (_textEditor is not null) { CommitTextEdit(); return; }

        var canvasPoint = e.GetPosition(_canvas);

        // Click-to-place tools don't drag.
        if (Tool == AnnotationTool.Text) { BeginTextEdit(canvasPoint); return; }
        if (Tool == AnnotationTool.StepBadge) { PlaceStepBadge(canvasPoint); return; }

        _dragging = true;
        _dragStart = canvasPoint;
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

    private PointD ToImage(Point canvasPoint) => new(canvasPoint.X / Scale, canvasPoint.Y / Scale);

    // ---- click-to-place tools (text, step badges) ----

    /// <summary>Drop an in-place TextBox at the click; it commits to a <see cref="TextAnnotation"/>.</summary>
    private void BeginTextEdit(Point canvasPoint)
    {
        CancelTextEdit();
        _textOrigin = ToImage(canvasPoint);

        var editor = new TextBox
        {
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)),
            Foreground = ParseBrush(StrokeColorHex),
            BorderBrush = ParseBrush(StrokeColorHex),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 0),
            FontSize = TextFontSize * Scale,
            MinWidth = 40,
            AcceptsReturn = true,
            PlaceholderText = "Type…",
        };
        Canvas.SetLeft(editor, canvasPoint.X);
        Canvas.SetTop(editor, canvasPoint.Y);

        editor.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter && !ke.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                ke.Handled = true;
                CommitTextEdit();
            }
            else if (ke.Key == Key.Escape)
            {
                ke.Handled = true;
                CancelTextEdit();
            }
        };
        editor.LostFocus += (_, _) => CommitTextEdit();

        _textEditor = editor;
        _canvas.Children.Add(editor);
        editor.Focus();
    }

    private void CommitTextEdit()
    {
        if (_textEditor is null) return;
        var editor = _textEditor;
        _textEditor = null; // clear first so LostFocus during removal doesn't re-enter

        var text = editor.Text?.Trim();
        _canvas.Children.Remove(editor);

        if (!string.IsNullOrEmpty(text) && _document is not null)
            _document.Add(new TextAnnotation(_textOrigin.X, _textOrigin.Y, text, TextFontSize)
            { Color = StrokeColorHex, StrokeWidth = StrokeWidth });
    }

    private void CancelTextEdit()
    {
        if (_textEditor is null) return;
        var editor = _textEditor;
        _textEditor = null;
        _canvas.Children.Remove(editor);
    }

    /// <summary>Place the next sequential numbered badge, centred on the click.</summary>
    private void PlaceStepBadge(Point canvasPoint)
    {
        if (_document is null) return;
        var p = ToImage(canvasPoint);
        _document.Add(new StepBadgeAnnotation(p.X, p.Y, NextStepNumber())
        { Color = StrokeColorHex, StrokeWidth = StrokeWidth });
    }

    /// <summary>Next badge number, derived from the document so undo/redo renumber for free.</summary>
    private int NextStepNumber()
    {
        if (_document is null) return 1;
        var highest = 0;
        foreach (var item in _document.Items)
            if (item is StepBadgeAnnotation b && b.Number > highest) highest = b.Number;
        return highest + 1;
    }

    private double BadgeDiameter(double strokeWidth) => 20 + strokeWidth * 2;

    // ---- zoom ----

    /// <summary>Ctrl+wheel zooms toward the cursor; a plain wheel scrolls (handled by the ScrollViewer).</summary>
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;
        var factor = e.Delta.Y > 0 ? ZoomStep : 1 / ZoomStep;
        ApplyZoom(Scale * factor, e.GetPosition(this));
    }

    /// <summary>Zoom in one step about the viewport centre.</summary>
    public void ZoomIn() => ApplyZoom(Scale * ZoomStep, ViewportCentre());

    /// <summary>Zoom out one step about the viewport centre.</summary>
    public void ZoomOut() => ApplyZoom(Scale / ZoomStep, ViewportCentre());

    /// <summary>Snap to 100% (one image pixel per DIP), about the viewport centre.</summary>
    public void ZoomToActual() => ApplyZoom(1.0, ViewportCentre());

    /// <summary>Return to auto-fit mode.</summary>
    public void ZoomToFit()
    {
        if (_image is null) return;
        _zoom = null;
        Layout();
        ZoomChanged?.Invoke();
    }

    private Point ViewportCentre() => new(Bounds.Width / 2, Bounds.Height / 2);

    /// <summary>
    /// Set an explicit zoom scale (clamped) and keep the image point under <paramref name="viewportAnchor"/>
    /// (a point in this control's coordinates) fixed, so zooming feels anchored to the cursor/centre.
    /// </summary>
    private void ApplyZoom(double target, Point viewportAnchor)
    {
        if (_image is null) return;

        var clamped = Math.Clamp(target, MinZoom, MaxZoom);
        var oldScale = Scale;
        if (Math.Abs(clamped - oldScale) < 0.0001) return;

        // Image point currently under the anchor (canvas coords → image coords).
        var canvasPoint = TranslateToCanvas(viewportAnchor);
        var imageX = canvasPoint.X / oldScale;
        var imageY = canvasPoint.Y / oldScale;

        _zoom = clamped;
        Layout();

        // After the canvas resizes, scroll so the same image point sits back under the anchor.
        Dispatcher.UIThread.Post(() =>
        {
            var newCanvasX = imageX * Scale;
            var newCanvasY = imageY * Scale;
            _scroller.Offset = new Vector(
                Math.Max(0, newCanvasX - viewportAnchor.X),
                Math.Max(0, newCanvasY - viewportAnchor.Y));
        }, DispatcherPriority.Render);

        ZoomChanged?.Invoke();
    }

    /// <summary>Map a point in this control's coordinates to canvas coordinates via the scroll offset.</summary>
    private Point TranslateToCanvas(Point viewportPoint)
    {
        // The canvas may be centred (smaller than viewport) or scrolled (larger). Offset accounts for
        // scrolling; when centred, the canvas origin sits at (viewport - canvas)/2.
        var offset = _scroller.Offset;
        var extentW = _canvas.Bounds.Width;
        var extentH = _canvas.Bounds.Height;
        var padX = extentW < Bounds.Width ? (Bounds.Width - extentW) / 2 : 0;
        var padY = extentH < Bounds.Height ? (Bounds.Height - extentH) / 2 : 0;
        return new Point(viewportPoint.X - padX + offset.X, viewportPoint.Y - padY + offset.Y);
    }

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
        var thickness = StrokeWidth * Scale;
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
        foreach (var pt in _freehand) poly.Points.Add(new Point(pt.X * Scale, pt.Y * Scale));
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
            case TextAnnotation tx:
                var text = new TextBlock
                {
                    Text = tx.Text,
                    Foreground = ParseBrush(tx.Color),
                    FontSize = tx.FontSize * scale,
                    FontWeight = FontWeight.SemiBold,
                };
                Canvas.SetLeft(text, tx.X * scale);
                Canvas.SetTop(text, tx.Y * scale);
                return text;
            case StepBadgeAnnotation sb:
                return BuildBadge(sb, scale);
            default:
                return null;
        }
    }

    /// <summary>A filled circle with a centred number, centred on the badge's (X,Y) image point.</summary>
    private Control BuildBadge(StepBadgeAnnotation badge, double scale)
    {
        var diameter = BadgeDiameter(badge.StrokeWidth) * scale;
        var color = Color.Parse(badge.Color);
        var panel = new Panel { Width = diameter, Height = diameter };
        panel.Children.Add(new Ellipse { Fill = new SolidColorBrush(color), Width = diameter, Height = diameter });
        panel.Children.Add(new TextBlock
        {
            Text = badge.Number.ToString(),
            Foreground = ContrastText(color),
            FontSize = diameter * 0.5,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        Canvas.SetLeft(panel, badge.X * scale - diameter / 2);
        Canvas.SetTop(panel, badge.Y * scale - diameter / 2);
        return panel;
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

    /// <summary>Black or white, whichever reads better on <paramref name="background"/>.</summary>
    private static IBrush ContrastText(Color background)
    {
        var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255;
        return luminance > 0.55 ? Brushes.Black : Brushes.White;
    }

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
