using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Shrike.App.Native;
using Shrike.Core.Capture;
using Path = Avalonia.Controls.Shapes.Path;

namespace Shrike.App.Views;

/// <summary>
/// The recording region frame, from setup through to the live recording. It covers the region's monitor
/// with a dim scrim cut out over the chosen rectangle and, in the <b>setup</b> phase, lets the user nudge
/// that rectangle with eight resize handles (or drag its interior). Nothing is captured yet. The HUD's
/// Record button drives <see cref="StartCountdown"/>: a 3-2-1 count over the region, after which
/// <see cref="CountdownFinished"/> fires. The controller then calls <see cref="EnterRecordingMode"/>,
/// which strips the setup chrome and turns this into a plain amber frame — click-through and excluded
/// from capture, exactly like the old standalone border — so it shows on screen but never in the file.
/// Staying one window (rather than closing and opening a border) keeps the frame from flickering when
/// recording begins.
/// </summary>
public sealed class RecordingRegionWindow : Window
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
    private readonly Border _sizePill;
    private readonly TextBlock _sizeText = new() { Foreground = new SolidColorBrush(Color.Parse("#EDE6DA")), FontSize = 12 };
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
    private bool _recording;
    private DispatcherTimer? _timer;

    /// <summary>The live region in physical pixels — the final value the recorder is built from.</summary>
    public PixelBounds Region => _region;

    /// <summary>Raised whenever the user resizes/moves the region during setup.</summary>
    public event Action<PixelBounds>? RegionChanged;

    /// <summary>Raised when the 3-2-1 countdown completes and recording should begin.</summary>
    public event Action? CountdownFinished;

    /// <summary>Raised if the user presses Esc during setup.</summary>
    public event Action? Cancelled;

    // Parameterless ctor for the XAML designer only.
    public RecordingRegionWindow()
        : this(new PixelBounds(200, 200, 600, 400),
               [new MonitorInfo(new PixelBounds(0, 0, 1920, 1080), 1.0, true)])
    {
    }

    internal RecordingRegionWindow(PixelBounds region, IReadOnlyList<MonitorInfo> monitors)
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
        ShowActivated = false;   // the HUD keeps focus; the frame only takes the mouse (see OnOpened)
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

        _sizePill = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F2140F0A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#F5A524")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 3),
            IsHitTestVisible = false,
            Child = _sizeText,
        };

        _root.Children.Add(_scrim);
        _root.Children.Add(_border);
        foreach (var h in _handles) _root.Children.Add(h);
        _root.Children.Add(_sizePill);
        _root.Children.Add(_countdown);
        Content = _root;
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

        // Take the mouse (for handle drags) but never activation — so clicking the frame never raises it
        // above the HUD that floats over its scrim. The HUD owns keyboard shortcuts instead.
        if (OperatingSystem.IsWindows())
            WindowExclusion.MakeNonActivating(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_counting || _recording) return;
        if (e.Key == Key.Escape) Cancelled?.Invoke();
        else if (e.Key is Key.Enter or Key.Return) StartCountdown();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_counting || _recording) return;

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
        if (_counting || _recording) return;

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
        RegionChanged?.Invoke(_region);
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

    // ---- countdown → recording ----

    /// <summary>Run the 3-2-1 countdown over the region, then raise <see cref="CountdownFinished"/>. The
    /// region is frozen (handles gone) for the duration.</summary>
    public void StartCountdown()
    {
        if (_counting || _recording) return;
        _counting = true;
        _grip = Grip.None;
        Cursor = new Cursor(StandardCursorType.Arrow);

        // Strip the setup chrome so the user sees the real screen they're about to record, with just the
        // amber frame and a big count over it.
        _scrim.IsVisible = false;
        _sizePill.IsVisible = false;
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
                _counting = false;
                _countdown.IsVisible = false;
                CountdownFinished?.Invoke();
            }
            else
            {
                ShowCount(remaining);
            }
        };
        _timer.Start();
    }

    /// <summary>Turn this into the passive recording frame: amber border only, click-through, and hidden
    /// from capture so it shows on screen but never in the recording.</summary>
    public void EnterRecordingMode()
    {
        _recording = true;
        _counting = false;
        _timer?.Stop();
        _countdown.IsVisible = false;
        _scrim.IsVisible = false;
        _sizePill.IsVisible = false;
        foreach (var h in _handles) h.IsVisible = false;

        if (OperatingSystem.IsWindows())
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            WindowExclusion.Hide(hwnd);            // keep the frame out of the recording
            WindowExclusion.MakeClickThrough(hwnd); // let clicks fall through to the app underneath
        }
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
        _sizePill.Measure(Size.Infinity);
        Canvas.SetLeft(_sizePill, Math.Clamp(r.X, 4, Math.Max(4, Width - _sizePill.DesiredSize.Width - 4)));
        Canvas.SetTop(_sizePill, Math.Max(4, r.Y - _sizePill.DesiredSize.Height - 6));
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

    /// <summary>Map a physical-pixel rect into this monitor's local DIP coordinates.</summary>
    private Rect ToLocal(PixelBounds b) => new(
        (b.X - _monitor.Bounds.X) / _scale,
        (b.Y - _monitor.Bounds.Y) / _scale,
        b.Width / _scale,
        b.Height / _scale);
}
