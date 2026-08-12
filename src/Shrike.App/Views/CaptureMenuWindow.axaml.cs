using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Shrike.App.Views;

/// <summary>The capture mode a user picked from the chooser.</summary>
internal enum CaptureMenuChoice
{
    Region,
    Monitor,
    AllMonitors,
}

/// <summary>
/// A small popup that appears at the cursor when the single capture hotkey fires, letting the user
/// pick a capture mode (1–3 or click; Esc / click-away cancels). This is deliberately the one entry
/// point — the same chooser will grow a "Record" option when video capture lands.
/// </summary>
public partial class CaptureMenuWindow : Window
{
    private readonly PixelPoint _anchor;
    private bool _done;

    internal event Action<CaptureMenuChoice>? Chosen;
    internal event Action? Cancelled;

    // Parameterless ctor for the XAML designer only.
    public CaptureMenuWindow() : this(new PixelPoint(0, 0)) { }

    internal CaptureMenuWindow(PixelPoint anchor)
    {
        _anchor = anchor;
        InitializeComponent();
        Deactivated += (_, _) => Cancel(); // clicking away dismisses the chooser
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Sit just off the cursor, nudged back onto the screen if we'd overflow its right/bottom edge.
        var screen = Screens.ScreenFromPoint(_anchor);
        var pos = new PixelPoint(_anchor.X + 8, _anchor.Y + 8);
        if (screen is not null)
        {
            var wpx = (int)(Bounds.Width * screen.Scaling);
            var hpx = (int)(Bounds.Height * screen.Scaling);
            var b = screen.Bounds;
            pos = new PixelPoint(
                Math.Clamp(pos.X, b.X, Math.Max(b.X, b.Right - wpx)),
                Math.Clamp(pos.Y, b.Y, Math.Max(b.Y, b.Bottom - hpx)));
        }

        Position = pos;
        Activate();
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.D1 or Key.NumPad1: Choose(CaptureMenuChoice.Region); break;
            case Key.D2 or Key.NumPad2: Choose(CaptureMenuChoice.Monitor); break;
            case Key.D3 or Key.NumPad3: Choose(CaptureMenuChoice.AllMonitors); break;
            case Key.Escape: Cancel(); break;
        }
    }

    private void OnRegion(object? sender, RoutedEventArgs e) => Choose(CaptureMenuChoice.Region);
    private void OnMonitor(object? sender, RoutedEventArgs e) => Choose(CaptureMenuChoice.Monitor);
    private void OnAllMonitors(object? sender, RoutedEventArgs e) => Choose(CaptureMenuChoice.AllMonitors);

    private void Choose(CaptureMenuChoice choice)
    {
        if (_done) return;
        _done = true;
        Chosen?.Invoke(choice);
        Close();
    }

    private void Cancel()
    {
        if (_done) return;
        _done = true;
        Cancelled?.Invoke();
        Close();
    }
}
