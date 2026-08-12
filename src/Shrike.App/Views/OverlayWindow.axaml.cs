using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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
    private readonly RegionSelectionSession _session;
    private readonly MonitorInfo _monitor;
    private readonly Action _onSessionChanged;

    private Canvas? _root;
    private Path? _scrim;
    private Rectangle? _vLine;
    private Rectangle? _hLine;
    private Rectangle? _selBorder;
    private Border? _readoutPill;
    private TextBlock? _readout;
    private Border? _hintPill;

    // Parameterless ctor for the XAML designer only.
    public OverlayWindow()
        : this(new RegionSelectionSession(), new MonitorInfo(new PixelBounds(0, 0, 1920, 1080), 1.0, true))
    {
    }

    internal OverlayWindow(RegionSelectionSession session, MonitorInfo monitor)
    {
        _session = session;
        _monitor = monitor;
        _onSessionChanged = Render;

        InitializeComponent();

        _root = this.FindControl<Canvas>("Root");
        _scrim = this.FindControl<Path>("Scrim");
        _vLine = this.FindControl<Rectangle>("VLine");
        _hLine = this.FindControl<Rectangle>("HLine");
        _selBorder = this.FindControl<Rectangle>("SelBorder");
        _readoutPill = this.FindControl<Border>("ReadoutPill");
        _readout = this.FindControl<TextBlock>("Readout");
        _hintPill = this.FindControl<Border>("HintPill");

        _session.Changed += _onSessionChanged;
        Closed += (_, _) => _session.Changed -= _onSessionChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Place and size this window to exactly cover its monitor (physical origin; DIP size).
        Position = new PixelPoint(_monitor.Bounds.X, _monitor.Bounds.Y);
        Width = _monitor.Bounds.Width / _monitor.Scale;
        Height = _monitor.Bounds.Height / _monitor.Scale;

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

        if (_vLine is not null) Canvas.SetLeft(_vLine, p.X);
        if (_hLine is not null) Canvas.SetTop(_hLine, p.Y);

        if (_session.IsDragging)
            _session.Update(PhysicalX(p.X), PhysicalY(p.Y));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_session.IsDragging)
            return;

        var p = e.GetPosition(this);
        _session.Complete(PhysicalX(p.X), PhysicalY(p.Y));
    }

    private void Render()
    {
        var cur = _session.Current;
        if (!_session.IsDragging || cur is not { } selection || selection.Normalized().IsEmpty)
        {
            if (_selBorder is not null) _selBorder.IsVisible = false;
            if (_readoutPill is not null) _readoutPill.IsVisible = false;
            if (_hintPill is not null) _hintPill.IsVisible = !_session.IsDragging;
            UpdateScrim(null);
            return;
        }

        if (_hintPill is not null) _hintPill.IsVisible = false;

        var s = selection.Normalized();
        var lx = (s.X - _monitor.Bounds.X) / _monitor.Scale;
        var ly = (s.Y - _monitor.Bounds.Y) / _monitor.Scale;
        var lw = s.Width / _monitor.Scale;
        var lh = s.Height / _monitor.Scale;

        if (_selBorder is not null)
        {
            Canvas.SetLeft(_selBorder, lx);
            Canvas.SetTop(_selBorder, ly);
            _selBorder.Width = lw;
            _selBorder.Height = lh;
            _selBorder.IsVisible = true;
        }

        UpdateScrim(new Rect(lx, ly, lw, lh));

        // Show the size readout only on monitors the selection actually touches.
        var touchesThisMonitor = !_monitor.Bounds.Intersect(s).IsEmpty;
        if (touchesThisMonitor && _readout is not null && _readoutPill is not null)
        {
            _readout.Text = $"{s.Width} × {s.Height}";
            Canvas.SetLeft(_readoutPill, Math.Clamp(lx, 0, Math.Max(0, Width - 90)));
            Canvas.SetTop(_readoutPill, Math.Max(0, ly - 26));
            _readoutPill.IsVisible = true;
        }
        else if (_readoutPill is not null)
        {
            _readoutPill.IsVisible = false;
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

    private int PhysicalX(double dip) => _monitor.Bounds.X + (int)Math.Round(dip * _monitor.Scale);
    private int PhysicalY(double dip) => _monitor.Bounds.Y + (int)Math.Round(dip * _monitor.Scale);
}
