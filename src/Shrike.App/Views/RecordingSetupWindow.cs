using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shrike.Core.Capture;
using Path = Avalonia.Controls.Shapes.Path;

namespace Shrike.App.Views;

/// <summary>
/// The step between "you drew a region" and "recording starts". Covers the region's monitor with a dim
/// scrim, cut out over the chosen rectangle, and lets the user nudge that rectangle with eight resize
/// handles (or drag its interior to move it) until it's right. Nothing is captured yet. Pressing Record
/// runs a 3-2-1 countdown and then raises <see cref="Confirmed"/> with the final region; Esc / Cancel
/// raises <see cref="Cancelled"/>. Once the countdown begins the handles are gone — a recording, once
/// armed, can't be re-cropped, matching the brief.
/// </summary>
public sealed class RecordingSetupWindow : Window
{
    private enum Grip { None, Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    private const double HandleSize = 12;   // drawn handle square (DIP)
    private const double HandleHit = 11;    // grab half-extent around a handle centre (DIP)
    private const int MinRegion = 24;       // smallest region side, physical px

    private readonly MonitorInfo _monitor;
    private readonly double _scale;

    private PixelBounds _region;            // the live region, physical px

    // Transparent (not null) so the canvas catches pointer events everywhere — including the region's
    // hollow interior and over the handles (which are non-hit-testable; HitGrip does the picking).
    private readonly Canvas _root = new() { Background = Brushes.Transparent };
    private readonly Path _scrim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _border = new()
    {
        Stroke = new SolidColorBrush(Color.Parse("#F5A524")),
        StrokeThickness = 2,
        IsHitTestVisible = false,
    };
    private readonly Rectangle[] _handles;
    private readonly Border _toolbar;
    private readonly TextBlock _sizeText = new() { Foreground = new SolidColorBrush(Color.Parse("#B8AE9C")), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _countdown = new()
    {
        Foreground = new SolidColorBrush(Color.Parse("#F5A524")),
        FontSize = 132,
        FontWeight = FontWeight.Bold,
        IsHitTestVisible = false,
        IsVisible = false,
        Effect = new DropShadowEffect { BlurRadius = 24, OffsetX = 0, OffsetY = 0, Color = Colors.Black, Opacity = 0.8 },
    };

    private Grip _grip = Grip.None;
    private PixelBounds _dragOrigin;        // region when the current drag began
    private Point _dragStart;               // pointer (local DIP) where the drag began
    private bool _counting;
    private bool _done;
    private DispatcherTimer? _timer;

    /// <summary>Raised once with the final region (physical px) when the countdown completes.</summary>
    public event Action<PixelBounds>? Confirmed;

    /// <summary>Raised once if the user backs out before recording starts.</summary>
    public event Action? Cancelled;

    // Parameterless ctor for the XAML designer only.
    public RecordingSetupWindow()
        : this(new PixelBounds(200, 200, 600, 400),
               [new MonitorInfo(new PixelBounds(0, 0, 1920, 1080), 1.0, true)])
    {
    }

    internal RecordingSetupWindow(PixelBounds region, IReadOnlyList<MonitorInfo> monitors)
    {
        _region = region.Normalized();
        _monitor = MonitorFor(_region, monitors);
        _scale = _monitor.Scale <= 0 ? 1.0 : _monitor.Scale;

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _handles = new Rectangle[8];
        for (var i = 0; i < _handles.Length; i++)
            _handles[i] = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };

        _toolbar = BuildToolbar();

        _root.Children.Add(_scrim);
        _root.Children.Add(_border);
        foreach (var h in _handles) _root.Children.Add(h);
        _root.Children.Add(_countdown);
        _root.Children.Add(_toolbar);
        Content = _root;

        _toolbar.SizeChanged += (_, _) => PositionToolbar();
    }

    private Border BuildToolbar()
    {
        var record = new Button
        {
            Content = "● Record",
            Foreground = new SolidColorBrush(Color.Parse("#140F0A")),
            Background = new SolidColorBrush(Color.Parse("#F5A524")),
            Padding = new Thickness(14, 7),
            CornerRadius = new CornerRadius(7),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
        };
        record.Click += (_, _) => BeginCountdown();

        var cancel = new Button
        {
            Content = "Cancel",
            Foreground = new SolidColorBrush(Color.Parse("#EDE6DA")),
            Background = new SolidColorBrush(Color.Parse("#2A2318")),
            Padding = new Thickness(12, 7),
            CornerRadius = new CornerRadius(7),
            FontSize = 13,
        };
        cancel.Click += (_, _) => CancelSetup();

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F2140F0A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#F5A524")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(12, 8),
            BoxShadow = BoxShadows.Parse("0 14 36 -14 #000000"),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _sizeText, record, cancel },
            },
        };
    }

    private static MonitorInfo MonitorFor(PixelBounds region, IReadOnlyList<MonitorInfo> monitors)
    {
        var cx = region.X + region.Width / 2;
        var cy = region.Y + region.Height / 2;
        foreach (var m in monitors)
            if (cx >= m.Bounds.X && cx < m.Bounds.Right && cy >= m.Bounds.Y && cy < m.Bounds.Bottom)
                return m;
        return monitors.Count > 0 ? monitors[0] : new MonitorInfo(region, 1.0, true);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Position = new PixelPoint(_monitor.Bounds.X, _monitor.Bounds.Y);
        Width = _monitor.Bounds.Width / _scale;
        Height = _monitor.Bounds.Height / _scale;
        Redraw();
        Activate();
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_counting) return;
        if (e.Key == Key.Escape) CancelSetup();
        else if (e.Key is Key.Enter or Key.Return) BeginCountdown();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_counting) return;

        // Let the toolbar's buttons handle their own clicks.
        if (e.Source is Visual v && v.GetVisualAncestors().Contains(_toolbar)) return;

        var p = e.GetPosition(_root);
        _grip = HitGrip(p);
        if (_grip == Grip.None) return;

        _dragOrigin = _region;
        _dragStart = p;
        e.Pointer.Capture(_root);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_counting) return;

        var p = e.GetPosition(_root);
        if (_grip == Grip.None)
        {
            Cursor = CursorFor(HitGrip(p));
            return;
        }

        var ddx = (int)Math.Round((p.X - _dragStart.X) * _scale);
        var ddy = (int)Math.Round((p.Y - _dragStart.Y) * _scale);
        _region = Apply(_grip, _dragOrigin, ddx, ddy);
        Redraw();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _grip = Grip.None;
    }

    // ---- region maths (all physical px, clamped to the covered monitor) ----

    private PixelBounds Apply(Grip grip, PixelBounds o, int ddx, int ddy)
    {
        var mon = _monitor.Bounds;
        int left = o.X, top = o.Y, right = o.Right, bottom = o.Bottom;

        if (grip == Grip.Move)
        {
            var nx = Math.Clamp(left + ddx, mon.X, mon.Right - o.Width);
            var ny = Math.Clamp(top + ddy, mon.Y, mon.Bottom - o.Height);
            return new PixelBounds(nx, ny, o.Width, o.Height);
        }

        if (grip is Grip.Left or Grip.TopLeft or Grip.BottomLeft)
            left = Math.Clamp(left + ddx, mon.X, right - MinRegion);
        if (grip is Grip.Right or Grip.TopRight or Grip.BottomRight)
            right = Math.Clamp(right + ddx, left + MinRegion, mon.Right);
        if (grip is Grip.Top or Grip.TopLeft or Grip.TopRight)
            top = Math.Clamp(top + ddy, mon.Y, bottom - MinRegion);
        if (grip is Grip.Bottom or Grip.BottomLeft or Grip.BottomRight)
            bottom = Math.Clamp(bottom + ddy, top + MinRegion, mon.Bottom);

        return new PixelBounds(left, top, right - left, bottom - top);
    }

    private Grip HitGrip(Point p)
    {
        var r = ToLocal(_region);
        // Corners first, then edges (corners win where they overlap).
        var (cx0, cy0, cx1, cy1) = (r.X, r.Y, r.Right, r.Bottom);
        var mx = (cx0 + cx1) / 2;
        var my = (cy0 + cy1) / 2;

        if (Near(p, cx0, cy0)) return Grip.TopLeft;
        if (Near(p, cx1, cy0)) return Grip.TopRight;
        if (Near(p, cx0, cy1)) return Grip.BottomLeft;
        if (Near(p, cx1, cy1)) return Grip.BottomRight;
        if (Near(p, mx, cy0)) return Grip.Top;
        if (Near(p, mx, cy1)) return Grip.Bottom;
        if (Near(p, cx0, my)) return Grip.Left;
        if (Near(p, cx1, my)) return Grip.Right;
        if (p.X > cx0 && p.X < cx1 && p.Y > cy0 && p.Y < cy1) return Grip.Move;
        return Grip.None;
    }

    private static bool Near(Point p, double x, double y)
        => Math.Abs(p.X - x) <= HandleHit && Math.Abs(p.Y - y) <= HandleHit;

    private static Cursor CursorFor(Grip grip) => new(grip switch
    {
        Grip.Move => StandardCursorType.SizeAll,
        Grip.Left or Grip.Right => StandardCursorType.SizeWestEast,
        Grip.Top or Grip.Bottom => StandardCursorType.SizeNorthSouth,
        Grip.TopLeft or Grip.BottomRight => StandardCursorType.TopLeftCorner,
        Grip.TopRight or Grip.BottomLeft => StandardCursorType.TopRightCorner,
        _ => StandardCursorType.Arrow,
    });

    // ---- countdown ----

    private void BeginCountdown()
    {
        if (_counting || _done) return;
        _counting = true;
        _grip = Grip.None;
        Cursor = new Cursor(StandardCursorType.Arrow);

        // Strip the setup chrome so the user sees the real screen they're about to record, with just the
        // amber frame and a big count over it.
        _scrim.IsVisible = false;
        _toolbar.IsVisible = false;
        foreach (var h in _handles) h.IsVisible = false;

        var remaining = 3;
        ShowCount(remaining);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining <= 0)
            {
                _timer!.Stop();
                Finish();
            }
            else
            {
                ShowCount(remaining);
            }
        };
        _timer.Start();
    }

    private void ShowCount(int n)
    {
        _countdown.Text = n.ToString();
        _countdown.IsVisible = true;
        _countdown.Measure(Size.Infinity);
        var r = ToLocal(_region);
        var ds = _countdown.DesiredSize;
        Canvas.SetLeft(_countdown, r.X + (r.Width - ds.Width) / 2);
        Canvas.SetTop(_countdown, r.Y + (r.Height - ds.Height) / 2);
    }

    private void Finish()
    {
        if (_done) return;
        _done = true;
        var region = _region;
        Close();
        Confirmed?.Invoke(region);
    }

    private void CancelSetup()
    {
        if (_done) return;
        _done = true;
        _timer?.Stop();
        Close();
        Cancelled?.Invoke();
    }

    // ---- drawing ----

    private void Redraw()
    {
        var r = ToLocal(_region);

        Canvas.SetLeft(_border, r.X);
        Canvas.SetTop(_border, r.Y);
        _border.Width = r.Width;
        _border.Height = r.Height;

        var full = new RectangleGeometry(new Rect(0, 0, Width, Height));
        _scrim.Data = r.Width > 0 && r.Height > 0
            ? new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(r))
            : full;

        PlaceHandles(r);

        _sizeText.Text = $"{_region.Width} × {_region.Height}";
        PositionToolbar();
    }

    private void PlaceHandles(Rect r)
    {
        var mx = r.X + r.Width / 2;
        var my = r.Y + r.Height / 2;
        Span<(double X, double Y)> pts =
        [
            (r.X, r.Y), (mx, r.Y), (r.Right, r.Y),
            (r.X, my), (r.Right, my),
            (r.X, r.Bottom), (mx, r.Bottom), (r.Right, r.Bottom),
        ];
        for (var i = 0; i < _handles.Length; i++)
        {
            Canvas.SetLeft(_handles[i], pts[i].X - HandleSize / 2);
            Canvas.SetTop(_handles[i], pts[i].Y - HandleSize / 2);
        }
    }

    private void PositionToolbar()
    {
        if (_counting) return;
        var r = ToLocal(_region);
        var tw = _toolbar.Bounds.Width;
        var th = _toolbar.Bounds.Height;
        const double gap = 12;

        var x = Math.Clamp(r.X + (r.Width - tw) / 2, 4, Math.Max(4, Width - tw - 4));

        double y;
        if (r.Bottom + gap + th <= Height) y = r.Bottom + gap;   // below the region
        else if (r.Y - gap - th >= 0) y = r.Y - gap - th;        // above it
        else y = Math.Max(4, Height - th - 4);                   // last resort

        Canvas.SetLeft(_toolbar, x);
        Canvas.SetTop(_toolbar, y);
    }

    /// <summary>Map a physical-pixel rect into this monitor's local DIP coordinates.</summary>
    private Rect ToLocal(PixelBounds b) => new(
        (b.X - _monitor.Bounds.X) / _scale,
        (b.Y - _monitor.Bounds.Y) / _scale,
        b.Width / _scale,
        b.Height / _scale);
}
