using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Collections;
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
    Crop,
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

    // ---- select / move state (Select tool) ----
    private int _selectedIndex = -1;
    private bool _movingSelection;
    private Point _moveStartCanvas;
    private Annotation? _moveOriginal;
    private bool _moveCheckpointed;
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor SizeWECursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor SizeNSCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor TopLeftCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor TopRightCursor = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor BottomLeftCursor = new(StandardCursorType.BottomLeftCorner);
    private static readonly Cursor BottomRightCursor = new(StandardCursorType.BottomRightCorner);

    /// <summary>Export crop in image pixels, or null for the whole image. Non-destructive (applied on export).</summary>
    private RectD? _cropRect;

    /// <summary>Raised when the crop rectangle is set or cleared, so the editor can update the size readout.</summary>
    public event Action? CropChanged;

    // ---- crop handle drag state (Crop tool) ----
    /// <summary>Which part of the crop rect a drag is manipulating: an edge/corner handle, the interior (move), or nothing.</summary>
    private enum CropGrip { None, Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }
    private CropGrip _cropGrip = CropGrip.None;
    private RectD _cropOriginal;   // crop rect (image px) when the handle drag began
    private Point _cropDragStart;  // canvas point where the handle drag began

    private AnnotationTool _tool = AnnotationTool.None;

    /// <summary>Active tool. Switching tools commits any text edit in progress.</summary>
    public AnnotationTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            CommitTextEdit();
            ClearSelection();
            Cursor = Cursor.Default;
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
        _selectedIndex = -1;
        _cropRect = null;
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

        DrawSelectionOutline();
        DrawCropMask();
    }

    /// <summary>Dashed box around the selected annotation (drawn last, so it sits on top).</summary>
    private void DrawSelectionOutline()
    {
        if (_document is null || _selectedIndex < 0 || _selectedIndex >= _document.Items.Count) return;

        var b = AnnotationGeometry.Bounds(_document.Items[_selectedIndex]);
        const double pad = 4;
        var box = new Rectangle
        {
            Stroke = Brushes.White,
            StrokeThickness = 1,
            StrokeDashArray = new AvaloniaList<double> { 4, 3 },
            IsHitTestVisible = false,
        };
        PlaceBox(box,
            b.X * Scale - pad, b.Y * Scale - pad,
            b.Width * Scale + pad * 2, b.Height * Scale + pad * 2);
        _canvas.Children.Add(box);
    }

    // ---- drawing ----

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_document is null) return;

        // A text edit in progress commits on the next click elsewhere (like most editors).
        if (_textEditor is not null) { CommitTextEdit(); return; }

        var canvasPoint = e.GetPosition(_canvas);

        // Select tool: pick the annotation under the cursor and start a move if there is one.
        if (Tool == AnnotationTool.None) { BeginSelectOrMove(canvasPoint, e); return; }

        // Click-to-place tools don't drag.
        if (Tool == AnnotationTool.Text) { BeginTextEdit(canvasPoint); return; }
        if (Tool == AnnotationTool.StepBadge) { PlaceStepBadge(canvasPoint); return; }

        // Crop tool: grab a handle (resize) or the interior (move) of an existing crop; otherwise fall
        // through and draw a fresh crop rectangle.
        if (Tool == AnnotationTool.Crop && _cropRect is { } crop)
        {
            var grip = HitCropGrip(canvasPoint);
            if (grip != CropGrip.None)
            {
                _cropGrip = grip;
                _cropOriginal = crop;
                _cropDragStart = canvasPoint;
                e.Pointer.Capture(_canvas);
                return;
            }
        }

        _dragging = true;
        _dragStart = canvasPoint;
        _freehand = [ToImage(_dragStart)];
        e.Pointer.Capture(_canvas);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_movingSelection) { DragSelection(e.GetPosition(_canvas)); return; }

        // Crop handle drag: resize the grabbed edge/corner or move the whole rect, live.
        if (_cropGrip != CropGrip.None)
        {
            var cp = e.GetPosition(_canvas);
            var ddx = (cp.X - _cropDragStart.X) / Scale;
            var ddy = (cp.Y - _cropDragStart.Y) / Scale;
            _cropRect = _cropGrip == CropGrip.Move ? MoveCrop(ddx, ddy) : ResizeCrop(_cropGrip, ddx, ddy);
            Rerender();
            CropChanged?.Invoke();
            return;
        }

        // Select tool, hovering: show the move cursor over a selectable annotation.
        if (!_dragging && Tool == AnnotationTool.None)
        {
            Cursor = HitTestTopmost(ToImage(e.GetPosition(_canvas))) >= 0 ? MoveCursor : Cursor.Default;
            return;
        }

        // Crop tool, hovering an existing crop: show the resize/move cursor over its handles and interior.
        if (!_dragging && Tool == AnnotationTool.Crop && _cropRect is not null)
        {
            Cursor = CropCursor(HitCropGrip(e.GetPosition(_canvas)));
            return;
        }

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
        if (_movingSelection)
        {
            _movingSelection = false;
            _moveOriginal = null;
            _moveCheckpointed = false;
            return;
        }

        // Finish a crop handle drag (resize/move); the rect is already committed live.
        if (_cropGrip != CropGrip.None) { _cropGrip = CropGrip.None; return; }

        if (!_dragging || _document is null) return;
        _dragging = false;

        if (_preview is not null) { _canvas.Children.Remove(_preview); _preview = null; }

        var end = e.GetPosition(_canvas);

        if (Tool == AnnotationTool.Crop) { SetCropFromDrag(_dragStart, end); return; }

        var annotation = BuildAnnotation(_dragStart, end);
        if (annotation is not null)
            _document.Add(annotation); // Changed → Rerender paints the committed shape
    }

    private PointD ToImage(Point canvasPoint) => new(canvasPoint.X / Scale, canvasPoint.Y / Scale);

    // ---- select / move ----

    /// <summary>Select the topmost annotation under the click and arm a drag-move if one was hit.</summary>
    private void BeginSelectOrMove(Point canvasPoint, PointerPressedEventArgs e)
    {
        if (_document is null) return;

        var hit = HitTestTopmost(ToImage(canvasPoint));
        _selectedIndex = hit;
        Rerender(); // paint (or clear) the selection outline

        if (hit >= 0)
        {
            _movingSelection = true;
            _moveStartCanvas = canvasPoint;
            _moveOriginal = _document.Items[hit];
            _moveCheckpointed = false;
            e.Pointer.Capture(_canvas);
        }
    }

    /// <summary>Live-move the selected annotation, checkpointing undo once at the first real drag.</summary>
    private void DragSelection(Point canvasPoint)
    {
        if (_document is null || _moveOriginal is null) return;

        var dx = (canvasPoint.X - _moveStartCanvas.X) / Scale;
        var dy = (canvasPoint.Y - _moveStartCanvas.Y) / Scale;

        if (!_moveCheckpointed)
        {
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return; // ignore jitter — a click, not a drag
            _document.BeginInteractive();
            _moveCheckpointed = true;
        }

        _document.ReplaceLive(_selectedIndex, AnnotationGeometry.Translate(_moveOriginal, dx, dy));
    }

    /// <summary>Index of the topmost (last-drawn) annotation the point selects, or -1.</summary>
    private int HitTestTopmost(PointD imagePoint)
    {
        if (_document is null) return -1;
        var tol = 6 / Scale; // ~6 screen px, scale-independent
        for (var i = _document.Items.Count - 1; i >= 0; i--)
            if (AnnotationGeometry.HitTest(_document.Items[i], imagePoint, tol)) return i;
        return -1;
    }

    /// <summary>Clear the current selection (e.g. on tool switch, undo/redo, or new capture).</summary>
    public void ClearSelection()
    {
        if (_selectedIndex == -1) return;
        _selectedIndex = -1;
        Rerender();
    }

    /// <summary>Delete the selected annotation, if any (undoable).</summary>
    public void DeleteSelected()
    {
        if (_document is null || _selectedIndex < 0 || _selectedIndex >= _document.Items.Count) return;
        _document.RemoveAt(_selectedIndex); // Changed → Rerender
        _selectedIndex = -1;
    }

    // ---- crop (non-destructive; applied on export) ----

    /// <summary>Set the crop from a finished drag, clamped to the image; a tiny drag clears the crop.</summary>
    private void SetCropFromDrag(Point start, Point end)
    {
        if (_image is null) return;
        var a = ToImage(start);
        var b = ToImage(end);
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var w = Math.Abs(b.X - a.X);
        var h = Math.Abs(b.Y - a.Y);

        if (w < 5 || h < 5)
        {
            ClearCrop(); // treat a click / tiny drag as "remove the crop"
            return;
        }

        // Clamp to the image extent.
        x = Math.Clamp(x, 0, _image.Width);
        y = Math.Clamp(y, 0, _image.Height);
        w = Math.Min(w, _image.Width - x);
        h = Math.Min(h, _image.Height - y);

        _cropRect = new RectD(x, y, w, h);
        Rerender();
        CropChanged?.Invoke();
    }

    /// <summary>Radius (canvas px) within which a handle counts as grabbed, and its drawn size.</summary>
    private const double CropHandleHit = 9;
    private const double CropHandleSize = 8;

    /// <summary>The eight resize handles of a crop rect (canvas coords), each tagged with the grip it drives.</summary>
    private static IEnumerable<(CropGrip Grip, double X, double Y)> CropHandles(double cx, double cy, double cw, double ch)
    {
        var mx = cx + cw / 2;
        var my = cy + ch / 2;
        yield return (CropGrip.TopLeft, cx, cy);
        yield return (CropGrip.Top, mx, cy);
        yield return (CropGrip.TopRight, cx + cw, cy);
        yield return (CropGrip.Right, cx + cw, my);
        yield return (CropGrip.BottomRight, cx + cw, cy + ch);
        yield return (CropGrip.Bottom, mx, cy + ch);
        yield return (CropGrip.BottomLeft, cx, cy + ch);
        yield return (CropGrip.Left, cx, my);
    }

    /// <summary>Which grip a canvas point grabs: a handle if near one, else Move if inside the rect, else None.</summary>
    private CropGrip HitCropGrip(Point p)
    {
        if (_cropRect is not { } c) return CropGrip.None;
        var cx = c.X * Scale;
        var cy = c.Y * Scale;
        var cw = c.Width * Scale;
        var ch = c.Height * Scale;

        foreach (var (grip, hx, hy) in CropHandles(cx, cy, cw, ch))
            if (Math.Abs(p.X - hx) <= CropHandleHit && Math.Abs(p.Y - hy) <= CropHandleHit) return grip;

        if (p.X >= cx && p.X <= cx + cw && p.Y >= cy && p.Y <= cy + ch) return CropGrip.Move;
        return CropGrip.None;
    }

    /// <summary>The cursor for a grip: directional resize on a handle, move over the interior.</summary>
    private static Cursor CropCursor(CropGrip grip) => grip switch
    {
        CropGrip.Left or CropGrip.Right => SizeWECursor,
        CropGrip.Top or CropGrip.Bottom => SizeNSCursor,
        CropGrip.TopLeft => TopLeftCursor,
        CropGrip.TopRight => TopRightCursor,
        CropGrip.BottomLeft => BottomLeftCursor,
        CropGrip.BottomRight => BottomRightCursor,
        CropGrip.Move => MoveCursor,
        _ => Cursor.Default,
    };

    /// <summary>Resize the crop by moving the grabbed edge(s) by an image-pixel delta, clamped to the image with a min size.</summary>
    private RectD ResizeCrop(CropGrip grip, double dx, double dy)
    {
        const double min = 5;
        var x0 = _cropOriginal.X;
        var y0 = _cropOriginal.Y;
        var x1 = x0 + _cropOriginal.Width;
        var y1 = y0 + _cropOriginal.Height;
        var iw = (double)_image!.Width;
        var ih = (double)_image.Height;

        var left = grip is CropGrip.Left or CropGrip.TopLeft or CropGrip.BottomLeft;
        var right = grip is CropGrip.Right or CropGrip.TopRight or CropGrip.BottomRight;
        var top = grip is CropGrip.Top or CropGrip.TopLeft or CropGrip.TopRight;
        var bottom = grip is CropGrip.Bottom or CropGrip.BottomLeft or CropGrip.BottomRight;

        if (left) x0 = Math.Clamp(x0 + dx, 0, x1 - min);
        if (right) x1 = Math.Clamp(x1 + dx, x0 + min, iw);
        if (top) y0 = Math.Clamp(y0 + dy, 0, y1 - min);
        if (bottom) y1 = Math.Clamp(y1 + dy, y0 + min, ih);

        return new RectD(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Move the whole crop rect by an image-pixel delta, kept fully inside the image.</summary>
    private RectD MoveCrop(double dx, double dy)
    {
        var w = _cropOriginal.Width;
        var h = _cropOriginal.Height;
        var x = Math.Clamp(_cropOriginal.X + dx, 0, _image!.Width - w);
        var y = Math.Clamp(_cropOriginal.Y + dy, 0, _image.Height - h);
        return new RectD(x, y, w, h);
    }

    /// <summary>Remove the crop (back to the full image).</summary>
    public void ClearCrop()
    {
        if (_cropRect is null) return;
        _cropRect = null;
        Rerender();
        CropChanged?.Invoke();
    }

    /// <summary>Size (image px) the export will produce — the crop size if cropped, else the full image.</summary>
    public (int Width, int Height) EffectiveSize()
    {
        if (_cropRect is { } c) return ((int)Math.Round(c.Width), (int)Math.Round(c.Height));
        return _image is null ? (0, 0) : (_image.Width, _image.Height);
    }

    /// <summary>True when an export crop is active.</summary>
    public bool IsCropped => _cropRect is not null;

    /// <summary>Crop <paramref name="image"/> to the current crop rect (no-op when uncropped).</summary>
    public CapturedImage ApplyExportCrop(CapturedImage image)
    {
        if (_cropRect is not { } c || _image is null) return image;

        var x = (int)Math.Round(c.X);
        var y = (int)Math.Round(c.Y);
        var w = (int)Math.Round(c.Width);
        var h = (int)Math.Round(c.Height);
        if (w <= 0 || h <= 0) return image;

        // Crop() works in the image's Source (physical) space, so offset by the source origin.
        var region = new PixelBounds(image.Source.X + x, image.Source.Y + y, w, h);
        try { return image.Crop(region); }
        catch { return image; }
    }

    /// <summary>Dark mask over everything outside the crop, plus a bright border (drawn last, on top).</summary>
    private void DrawCropMask()
    {
        if (_cropRect is not { } c) return;

        var cx = c.X * Scale;
        var cy = c.Y * Scale;
        var cw = c.Width * Scale;
        var ch = c.Height * Scale;

        var full = new RectangleGeometry(new Rect(0, 0, _canvas.Width, _canvas.Height));
        var hole = new RectangleGeometry(new Rect(cx, cy, cw, ch));
        _canvas.Children.Add(new Path
        {
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, hole),
            Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
            IsHitTestVisible = false,
        });

        var border = new Rectangle { Stroke = Brushes.White, StrokeThickness = 1, IsHitTestVisible = false };
        PlaceBox(border, cx, cy, cw, ch);
        _canvas.Children.Add(border);

        // Resize handles, only while the Crop tool is active (they'd be noise under other tools).
        if (Tool != AnnotationTool.Crop) return;
        var handleStroke = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0));
        foreach (var (_, hx, hy) in CropHandles(cx, cy, cw, ch))
        {
            var handle = new Rectangle
            {
                Width = CropHandleSize,
                Height = CropHandleSize,
                Fill = Brushes.White,
                Stroke = handleStroke,
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(handle, hx - CropHandleSize / 2);
            Canvas.SetTop(handle, hy - CropHandleSize / 2);
            _canvas.Children.Add(handle);
        }
    }

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

    /// <summary>
    /// Next badge number: the lowest unused positive integer, so removing an earlier badge frees its
    /// number for the next one to reclaim (gaps fill in before the sequence grows).
    /// </summary>
    private int NextStepNumber()
    {
        if (_document is null) return 1;
        var used = new HashSet<int>();
        foreach (var item in _document.Items)
            if (item is StepBadgeAnnotation b) used.Add(b.Number);

        var n = 1;
        while (used.Contains(n)) n++;
        return n;
    }

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
            AnnotationTool.Crop => PlaceBox(new Rectangle
            {
                Stroke = Brushes.White,
                StrokeThickness = 1,
                StrokeDashArray = new AvaloniaList<double> { 4, 3 },
            }, x, y, w, h),
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
        var diameter = AnnotationGeometry.BadgeDiameter(badge.StrokeWidth) * scale;
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
