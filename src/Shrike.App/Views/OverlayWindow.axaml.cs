using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Shrike.Core.Interop;

namespace Shrike.App.Views;

/// <summary>
/// The M0 stub capture overlay: a borderless, topmost, translucent full-screen window with a
/// pointer-tracking crosshair. Its whole job in M0 is to prove the snappy path and the
/// no-desktop-switch rule — a fresh window is created per invocation, so it is always born on the
/// desktop the user is currently looking at. M1 turns this into the real region selector.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly VirtualDesktopService _desktops;
    private Rectangle? _vLine;
    private Rectangle? _hLine;
    private TextBlock? _desktopStatus;

    // Parameterless ctor kept for the XAML designer only.
    public OverlayWindow() : this(VirtualDesktopService.Create()) { }

    public OverlayWindow(VirtualDesktopService desktops)
    {
        _desktops = desktops;
        InitializeComponent();

        // Resolve named controls explicitly — robust across Avalonia's field-generator behaviour.
        _vLine = this.FindControl<Rectangle>("VLine");
        _hLine = this.FindControl<Rectangle>("HLine");
        _desktopStatus = this.FindControl<TextBlock>("DesktopStatus");
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Cover the primary screen. (M1 makes this per-monitor and driven by the pointer's screen.)
        var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is not null)
        {
            var bounds = screen.Bounds;               // physical pixels
            var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            Position = bounds.Position;               // PixelPoint (physical)
            Width = bounds.Width / scaling;           // DIPs
            Height = bounds.Height / scaling;

            if (_vLine is not null) _vLine.Height = Height;
            if (_hLine is not null) _hLine.Width = Width;
        }

        Activate();
        Focus();

        // Prove the headline feature's foundation: report whether we really landed on the current desktop.
        if (_desktopStatus is not null)
        {
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            _desktopStatus.Text = _desktops.IsWindowOnCurrentDesktop(handle) switch
            {
                true => "✓ on current virtual desktop — no switch",
                false => "⚠ not on current desktop (unexpected)",
                null => "virtual-desktop check unavailable",
            };
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            Close();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_vLine is not null) Canvas.SetLeft(_vLine, p.X);
        if (_hLine is not null) Canvas.SetTop(_hLine, p.Y);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // M0 stub: any click dismisses. M1 begins a region drag here instead.
        Close();
    }
}
