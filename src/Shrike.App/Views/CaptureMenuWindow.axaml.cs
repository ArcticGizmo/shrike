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
    Record,
    Pipette,
    Recent,
}

/// <summary>
/// A small popup that appears at the cursor when the single capture hotkey fires, letting the user pick a
/// capture mode (grouped as SCREENSHOT / RECORD / TOOLS; 1–6 or click; Esc / click-away cancels). It also
/// carries a self-timer "Delay" modifier (D cycles Off → 3 → 5 → 10s) that applies to the screenshot
/// modes only — the chosen delay is read back via <see cref="DelaySeconds"/> when a mode is picked.
/// </summary>
public partial class CaptureMenuWindow : Window
{
    // The delays we offer, in cycle order (0 = off). D steps through these and wraps.
    private static readonly int[] DelayCycle = [0, 3, 5, 10];

    private readonly PixelPoint _anchor;
    private readonly int _recentCount;
    private int _delaySeconds;
    private bool _done;

    internal event Action<CaptureMenuChoice>? Chosen;
    internal event Action? Cancelled;
    /// <summary>Raised whenever the delay modifier changes, so the caller can remember it.</summary>
    internal event Action<int>? DelayChanged;

    /// <summary>The self-timer delay currently armed, in seconds (0 = off). Read when a mode is chosen.</summary>
    internal int DelaySeconds => _delaySeconds;

    // Parameterless ctor for the XAML designer only.
    public CaptureMenuWindow() : this(new PixelPoint(0, 0)) { }

    internal CaptureMenuWindow(PixelPoint anchor, int recentCount = 0, int initialDelaySeconds = 0)
    {
        _anchor = anchor;
        _recentCount = recentCount;
        _delaySeconds = NormaliseDelay(initialDelaySeconds);
        InitializeComponent();

        // Recent opens the editor on the newest shot; greyed out until there's something to open.
        var recentButton = this.FindControl<Button>("RecentButton");
        if (recentButton is not null)
            recentButton.IsEnabled = _recentCount > 0;
        if (this.FindControl<TextBlock>("RecentText") is { } text)
            text.Text = _recentCount > 0 ? $"Recent captures ({_recentCount})" : "Recent captures";

        UpdateDelayUi();
    }

    private bool HasRecent => _recentCount > 0;

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
            case Key.D4 or Key.NumPad4: Choose(CaptureMenuChoice.Record); break;
            case Key.D5 or Key.NumPad5: Choose(CaptureMenuChoice.Pipette); break;
            case Key.D6 or Key.NumPad6 when HasRecent: Choose(CaptureMenuChoice.Recent); break;
            case Key.D: CycleDelay(); break;
            case Key.Escape: Cancel(); break;
        }
    }

    private void OnRegion(object? sender, RoutedEventArgs e) => Choose(CaptureMenuChoice.Region);
    private void OnMonitor(object? sender, RoutedEventArgs e) => Choose(CaptureMenuChoice.Monitor);
    private void OnAllMonitors(object? sender, RoutedEventArgs e) => Choose(CaptureMenuChoice.AllMonitors);
    private void OnRecord(object? sender, RoutedEventArgs e) => Choose(CaptureMenuChoice.Record);
    private void OnPickColour(object? sender, RoutedEventArgs e) => Choose(CaptureMenuChoice.Pipette);
    private void OnRecent(object? sender, RoutedEventArgs e) => Choose(CaptureMenuChoice.Recent);
    private void OnDelay(object? sender, RoutedEventArgs e) => CycleDelay();

    /// <summary>Step the delay to the next offered value (wrapping), refresh the UI, and notify.</summary>
    private void CycleDelay()
    {
        var i = Array.IndexOf(DelayCycle, _delaySeconds);
        _delaySeconds = DelayCycle[(i + 1) % DelayCycle.Length];
        UpdateDelayUi();
        DelayChanged?.Invoke(_delaySeconds);
    }

    /// <summary>Reflect the armed delay: the value on the Delay row and the +Ns badges on screenshot rows.</summary>
    private void UpdateDelayUi()
    {
        var armed = _delaySeconds > 0;
        var label = armed ? $"{_delaySeconds}s" : "Off";
        var badge = armed ? $"+{_delaySeconds}s" : "";

        if (this.FindControl<TextBlock>("DelayValue") is { } value)
        {
            value.Text = label;
            value.Foreground = armed
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F5A524"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8A7C68"));
        }

        foreach (var name in new[] { "RegionBadge", "MonitorBadge", "AllBadge" })
        {
            if (this.FindControl<TextBlock>(name) is { } b)
            {
                b.Text = badge;
                b.IsVisible = armed;
            }
        }
    }

    private static int NormaliseDelay(int seconds) => seconds is 3 or 5 or 10 ? seconds : 0;

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
