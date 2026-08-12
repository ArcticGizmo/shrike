using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Shrike.Core.Capture;
using Shrike.Core.Interop;
using Path = Avalonia.Controls.Shapes.Path;

namespace Shrike.App.Views;

/// <summary>
/// The region-selection overlay: a borderless, topmost, dimmed full-screen surface. Drag to select a
/// rectangle (dimming clears over the selection); release to capture; Esc to cancel. A fresh window is
/// created per invocation, so it is always born on the desktop the user is currently looking at.
/// </summary>
/// <remarks>M1 covers the primary screen. Per-monitor overlays and window snap-highlight are the
/// remaining M1 work; occlusion-correct window capture moves to M4 (Windows.Graphics.Capture).</remarks>
public partial class OverlayWindow : Window
{
    private readonly VirtualDesktopService _desktops;

    private Path? _scrim;
    private Rectangle? _vLine;
    private Rectangle? _hLine;
    private Rectangle? _selBorder;
    private Border? _readoutPill;
    private TextBlock? _readout;
    private Border? _hintPill;

    private PixelBounds _screenBounds; // physical pixels of the covered screen
    private double _scaling = 1.0;
    private Point? _dragStart;
    private bool _completed;           // guards against firing both selected + cancelled

    /// <summary>Raised with the chosen region in physical pixels.</summary>
    public event Action<PixelBounds>? RegionSelected;

    /// <summary>Raised when the user dismissed the overlay without selecting.</summary>
    public event Action? Cancelled;

    // Parameterless ctor kept for the XAML designer only.
    public OverlayWindow() : this(VirtualDesktopService.Create()) { }

    public OverlayWindow(VirtualDesktopService desktops)
    {
        _desktops = desktops;
        InitializeComponent();

        _scrim = this.FindControl<Path>("Scrim");
        _vLine = this.FindControl<Rectangle>("VLine");
        _hLine = this.FindControl<Rectangle>("HLine");
        _selBorder = this.FindControl<Rectangle>("SelBorder");
        _readoutPill = this.FindControl<Border>("ReadoutPill");
        _readout = this.FindControl<TextBlock>("Readout");
        _hintPill = this.FindControl<Border>("HintPill");
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is not null)
        {
            _screenBounds = new PixelBounds(
                screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
            _scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;

            Position = screen.Bounds.Position;
            Width = _screenBounds.Width / _scaling;
            Height = _screenBounds.Height / _scaling;
        }

        if (_vLine is not null) _vLine.Height = Height;
        if (_hLine is not null) _hLine.Width = Width;
        UpdateScrim(null);
        CentreHint();

        Activate();
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            Cancel();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);

        if (_vLine is not null) Canvas.SetLeft(_vLine, p.X);
        if (_hLine is not null) Canvas.SetTop(_hLine, p.Y);

        if (_dragStart is { } start)
            UpdateSelection(start, p);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragStart = e.GetPosition(this);
        if (_hintPill is not null) _hintPill.IsVisible = false;
        if (_selBorder is not null) _selBorder.IsVisible = true;
        if (_readoutPill is not null) _readoutPill.IsVisible = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart is not { } start)
            return;

        var end = e.GetPosition(this);
        _dragStart = null;

        var region = ToPhysical(start, end);
        if (region.Width < 2 || region.Height < 2)
        {
            Cancel(); // a click / tiny drag cancels rather than capturing a sliver
            return;
        }

        _completed = true;
        RegionSelected?.Invoke(region);
        Close();
    }

    private void Cancel()
    {
        if (_completed) return;
        _completed = true;
        Cancelled?.Invoke();
        Close();
    }

    private void UpdateSelection(Point start, Point current)
    {
        var x = Math.Min(start.X, current.X);
        var y = Math.Min(start.Y, current.Y);
        var w = Math.Abs(current.X - start.X);
        var h = Math.Abs(current.Y - start.Y);
        var rect = new Rect(x, y, w, h);

        if (_selBorder is not null)
        {
            Canvas.SetLeft(_selBorder, x);
            Canvas.SetTop(_selBorder, y);
            _selBorder.Width = w;
            _selBorder.Height = h;
        }

        UpdateScrim(rect);

        // Readout shows physical pixel dimensions (what the file/clipboard will actually be).
        var physical = ToPhysical(start, current);
        if (_readout is not null) _readout.Text = $"{physical.Width} × {physical.Height}";
        if (_readoutPill is not null)
        {
            Canvas.SetLeft(_readoutPill, Math.Clamp(x, 0, Math.Max(0, Width - 90)));
            Canvas.SetTop(_readoutPill, Math.Max(0, y - 26));
        }
    }

    private void UpdateScrim(Rect? hole)
    {
        if (_scrim is null) return;

        var full = new RectangleGeometry(new Rect(0, 0, Width, Height));
        _scrim.Data = hole is { } h && h.Width > 0 && h.Height > 0
            ? new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(h))
            : full;
    }

    private void CentreHint()
    {
        if (_hintPill is null) return;
        _hintPill.Measure(new Size(Width, Height));
        var size = _hintPill.DesiredSize;
        Canvas.SetLeft(_hintPill, (Width - size.Width) / 2);
        Canvas.SetTop(_hintPill, Height * 0.12);
    }

    /// <summary>Convert two window (DIP) points to a physical-pixel region on the covered screen.</summary>
    private PixelBounds ToPhysical(Point a, Point b)
    {
        int X(double dip) => _screenBounds.X + (int)Math.Round(dip * _scaling);
        int Y(double dip) => _screenBounds.Y + (int)Math.Round(dip * _scaling);
        return PixelBounds.FromCorners(X(a.X), Y(a.Y), X(b.X), Y(b.Y));
    }
}
