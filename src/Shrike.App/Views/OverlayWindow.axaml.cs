using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Shrike.App.Imaging;
using Shrike.App.Native;
using Shrike.App.Services;
using Shrike.Core.Capture;
using Path = Avalonia.Controls.Shapes.Path;

namespace Shrike.App.Views;

/// <summary>
/// One region-selection overlay, covering a single monitor. All overlays in a capture share a
/// <see cref="RegionSelectionSession"/> (physical-pixel state), so a drag that crosses monitors is
/// coherent: each window reports pointer positions in physical pixels and renders the shared
/// selection mapped back into its own DIP space. A fresh set is created per invocation, so overlays
/// always appear on the current virtual desktop.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int LoupeSampleHalf = 11;   // sample (2*half+1) px around the cursor
    private const double LoupeWidth = 164;    // approx loupe box incl. padding
    private const double LoupeHeight = 176;

    private readonly RegionSelectionSession _session;
    private readonly MonitorInfo _monitor;
    private readonly CapturedImage? _frozen; // frozen full-screen grab for the magnifier
    private readonly IReadOnlyList<PixelBounds> _windows; // for snap-highlight, topmost first
    private readonly Action _onSessionChanged;

    private Canvas? _root;
    private Path? _scrim;
    private Rectangle? _vLine;
    private Rectangle? _hLine;
    private Rectangle? _snapBorder;
    private Rectangle? _selBorder;
    private Border? _readoutPill;
    private TextBlock? _readout;
    private Border? _loupe;
    private Image? _loupeImage;
    private TextBlock? _loupeCoords;

    // Parameterless ctor for the XAML designer only.
    public OverlayWindow()
        : this(new RegionSelectionSession(), new MonitorInfo(new PixelBounds(0, 0, 1920, 1080), 1.0, true), null, [])
    {
    }

    internal OverlayWindow(
        RegionSelectionSession session, MonitorInfo monitor, CapturedImage? frozen,
        IReadOnlyList<PixelBounds> windows)
    {
        _session = session;
        _monitor = monitor;
        _frozen = frozen;
        _windows = windows;
        _onSessionChanged = Render;

        InitializeComponent();

        _root = this.FindControl<Canvas>("Root");
        _scrim = this.FindControl<Path>("Scrim");
        _vLine = this.FindControl<Rectangle>("VLine");
        _hLine = this.FindControl<Rectangle>("HLine");
        _snapBorder = this.FindControl<Rectangle>("SnapBorder");
        _selBorder = this.FindControl<Rectangle>("SelBorder");
        _readoutPill = this.FindControl<Border>("ReadoutPill");
        _readout = this.FindControl<TextBlock>("Readout");
        _loupe = this.FindControl<Border>("Loupe");
        _loupeImage = this.FindControl<Image>("LoupeImage");
        _loupeCoords = this.FindControl<TextBlock>("LoupeCoords");

        if (_loupeImage is not null)
            RenderOptions.SetBitmapInterpolationMode(_loupeImage, BitmapInterpolationMode.None);

        _session.Changed += _onSessionChanged;
        Closed += (_, _) => _session.Changed -= _onSessionChanged;

        // Size + position the window to cover its monitor BEFORE it is shown, so it never paints a
        // default-geometry frame first (which flashes the scrim + hint pill in the wrong place).
        ApplyMonitorLayout();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Place and size this window to exactly cover its monitor (physical origin; DIP size).</summary>
    private void ApplyMonitorLayout()
    {
        Position = new PixelPoint(_monitor.Bounds.X, _monitor.Bounds.Y);
        Width = _monitor.Bounds.Width / _monitor.Scale;
        Height = _monitor.Bounds.Height / _monitor.Scale;

        if (_vLine is not null) _vLine.Height = Height;
        if (_hLine is not null) _hLine.Width = Width;
        UpdateScrim(null);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyMonitorLayout(); // re-assert in case the pre-show geometry didn't stick on this platform
        Activate();
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            _session.Cancel();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // Capture so this window keeps receiving moves even when the pointer crosses to another monitor.
        if (_root is not null)
            e.Pointer.Capture(_root);

        var p = e.GetPosition(this);
        _session.Begin(PhysicalX(p.X), PhysicalY(p.Y));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);

        if (_vLine is not null) { Canvas.SetLeft(_vLine, p.X); _vLine.IsVisible = true; }
        if (_hLine is not null) { Canvas.SetTop(_hLine, p.Y); _hLine.IsVisible = true; }

        UpdateLoupe(p);

        if (_session.IsDragging)
        {
            _session.Update(PhysicalX(p.X), PhysicalY(p.Y));
        }
        else
        {
            // Hovering: highlight the window under the cursor for click-to-capture. Over bare desktop
            // (no window) fall back to this monitor, so a click grabs just this screen rather than all.
            var window = TopLevelWindows.TopmostAt(_windows, PhysicalX(p.X), PhysicalY(p.Y));
            _session.SetSnapCandidate(window ?? _monitor.Bounds);
        }
    }

    private void UpdateLoupe(Point local)
    {
        if (_frozen is null || _loupe is null || _loupeImage is null)
            return;

        var cx = PhysicalX(local.X);
        var cy = PhysicalY(local.Y);
        var size = LoupeSampleHalf * 2 + 1;

        CapturedImage sample;
        try
        {
            sample = _frozen.Crop(new PixelBounds(cx - LoupeSampleHalf, cy - LoupeSampleHalf, size, size));
        }
        catch
        {
            _loupe.IsVisible = false;
            return;
        }

        _loupeImage.Source = BitmapConverter.ToBitmap(sample);
        if (_loupeCoords is not null) _loupeCoords.Text = $"{cx}, {cy}";

        // Sit the loupe near the cursor, flipping away from the screen edges.
        var lx = local.X + 24;
        var ly = local.Y + 24;
        if (lx + LoupeWidth > Width) lx = local.X - LoupeWidth - 4;
        if (ly + LoupeHeight > Height) ly = local.Y - LoupeHeight - 4;
        Canvas.SetLeft(_loupe, Math.Clamp(lx, 0, Math.Max(0, Width - LoupeWidth)));
        Canvas.SetTop(_loupe, Math.Clamp(ly, 0, Math.Max(0, Height - LoupeHeight)));
        _loupe.IsVisible = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_session.IsDragging)
            return;

        var p = e.GetPosition(this);
        _session.Complete(PhysicalX(p.X), PhysicalY(p.Y));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        // The pointer left this monitor — clear its crosshair + loupe so they don't linger behind
        // while the overlay on the new monitor takes over. (During a drag the pointer is captured,
        // so exit doesn't fire and the loupe correctly follows the drag.)
        if (_vLine is not null) _vLine.IsVisible = false;
        if (_hLine is not null) _hLine.IsVisible = false;
        if (_loupe is not null) _loupe.IsVisible = false;
    }

    private void Render()
    {
        // Active drag with real area → selection mode.
        if (_session.IsDragging
            && _session.Current is { } current
            && !current.Normalized().IsEmpty)
        {
            DrawSelection(current.Normalized());
            SetSnapBorder(null);
            return;
        }

        // Not selecting: clear selection visuals.
        if (_selBorder is not null) _selBorder.IsVisible = false;
        if (_readoutPill is not null) _readoutPill.IsVisible = false;

        // Hovering a window (not dragging) → highlight only its border; the scrim stays uniform so
        // the other monitors' brightness doesn't change (the crosshair already shows where you are).
        UpdateScrim(null);
        var snap = _session.IsDragging ? null : _session.SnapCandidate;
        if (snap is { } window && !window.Normalized().IsEmpty)
            SetSnapBorder(window.Normalized());
        else
            SetSnapBorder(null);
    }

    private void DrawSelection(PixelBounds s)
    {
        var r = ToLocalRect(s);
        if (_selBorder is not null)
        {
            Canvas.SetLeft(_selBorder, r.X);
            Canvas.SetTop(_selBorder, r.Y);
            _selBorder.Width = r.Width;
            _selBorder.Height = r.Height;
            _selBorder.IsVisible = true;
        }

        UpdateScrim(r);

        // Size readout, only on monitors the selection actually touches.
        if (!_monitor.Bounds.Intersect(s).IsEmpty && _readout is not null && _readoutPill is not null)
        {
            _readout.Text = $"{s.Width} × {s.Height}";
            Canvas.SetLeft(_readoutPill, Math.Clamp(r.X, 0, Math.Max(0, Width - 90)));
            Canvas.SetTop(_readoutPill, Math.Max(0, r.Y - 26));
            _readoutPill.IsVisible = true;
        }
        else if (_readoutPill is not null)
        {
            _readoutPill.IsVisible = false;
        }
    }

    private void SetSnapBorder(PixelBounds? window)
    {
        if (_snapBorder is null) return;
        if (window is { } w && !w.Normalized().IsEmpty)
        {
            var r = ToLocalRect(w.Normalized());
            Canvas.SetLeft(_snapBorder, r.X);
            Canvas.SetTop(_snapBorder, r.Y);
            _snapBorder.Width = r.Width;
            _snapBorder.Height = r.Height;
            _snapBorder.IsVisible = true;
        }
        else
        {
            _snapBorder.IsVisible = false;
        }
    }

    /// <summary>Map a physical-pixel rect into this monitor's local DIP coordinates.</summary>
    private Rect ToLocalRect(PixelBounds b) => new(
        (b.X - _monitor.Bounds.X) / _monitor.Scale,
        (b.Y - _monitor.Bounds.Y) / _monitor.Scale,
        b.Width / _monitor.Scale,
        b.Height / _monitor.Scale);

    private void UpdateScrim(Rect? hole)
    {
        if (_scrim is null) return;

        var full = new RectangleGeometry(new Rect(0, 0, Width, Height));
        _scrim.Data = hole is { } h && h.Width > 0 && h.Height > 0
            ? new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(h))
            : full;
    }

    private int PhysicalX(double dip) => _monitor.Bounds.X + (int)Math.Round(dip * _monitor.Scale);
    private int PhysicalY(double dip) => _monitor.Bounds.Y + (int)Math.Round(dip * _monitor.Scale);
}
