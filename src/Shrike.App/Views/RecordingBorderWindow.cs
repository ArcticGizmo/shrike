using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Shrike.App.Native;
using Shrike.Core.Capture;

namespace Shrike.App.Views;

/// <summary>
/// A thin amber frame drawn around the region being recorded, so you can see exactly what's captured.
/// It covers the region with a transparent centre and a border-only stroke, is click-through (so it never
/// intercepts the mouse), and — crucially — is excluded from capture, so the frame itself shows on the
/// physical display but never lands in the recording. Born on the current desktop like the HUD.
/// </summary>
public sealed class RecordingBorderWindow : Window
{
    private const double Thickness = 3;
    private readonly PixelBounds _region;

    public RecordingBorderWindow(PixelBounds region)
    {
        _region = region;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;                 // never steal focus from the app being recorded
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#F5A524")),
            BorderThickness = new Thickness(Thickness),
            Background = Brushes.Transparent,
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var center = new PixelPoint(_region.X + _region.Width / 2, _region.Y + _region.Height / 2);
        var screen = Screens.ScreenFromPoint(center) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        var scale = screen?.Scaling ?? 1.0;

        Width = _region.Width / scale;
        Height = _region.Height / scale;
        Position = new PixelPoint(_region.X, _region.Y);

        if (OperatingSystem.IsWindows())
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            WindowExclusion.Hide(hwnd);            // keep the frame out of the recording
            WindowExclusion.MakeClickThrough(hwnd); // let clicks fall through to the app underneath
        }
    }
}
