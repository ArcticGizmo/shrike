using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Shrike.Core.Capture;

namespace Shrike.App.Views;

/// <summary>
/// A borderless, topmost scrim covering one monitor. Shown behind the capture chooser so the whole
/// desktop dims — the same cue as a live capture — signalling that an action is required. Clicking a
/// dimmer (i.e. anywhere outside the chooser) dismisses the chooser.
/// </summary>
public partial class DimWindow : Window
{
    private readonly MonitorInfo _monitor;

    /// <summary>Raised when the user clicks the scrim (a click-away that should cancel the chooser).</summary>
    public event Action? Dismissed;

    // Parameterless ctor for the XAML designer only.
    public DimWindow() : this(new MonitorInfo(new PixelBounds(0, 0, 1920, 1080), 1.0, true)) { }

    internal DimWindow(MonitorInfo monitor)
    {
        _monitor = monitor;
        InitializeComponent();

        // Size + position before Show so the scrim never flashes at a default geometry first.
        ApplyMonitorLayout();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Cover the monitor exactly (physical origin; DIP size).</summary>
    private void ApplyMonitorLayout()
    {
        Position = new PixelPoint(_monitor.Bounds.X, _monitor.Bounds.Y);
        Width = _monitor.Bounds.Width / _monitor.Scale;
        Height = _monitor.Bounds.Height / _monitor.Scale;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyMonitorLayout(); // re-assert in case the pre-show geometry didn't stick on this platform
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Dismissed?.Invoke();
    }
}
