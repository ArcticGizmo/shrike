using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Shrike.App.Views;

/// <summary>
/// A small, self-dismissing notice near the bottom of the screen — used for transient recording
/// messages (e.g. "FFmpeg needed"). Borderless, topmost, click- or timeout-dismissed. Built in code so
/// it needs no styles wired up; born on the current desktop like every other Shrike surface.
/// </summary>
public sealed class ToastWindow : Window
{
    private readonly DispatcherTimer _life;

    private ToastWindow(string message)
    {
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F2140F0A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#F5A524")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 11),
            BoxShadow = BoxShadows.Parse("0 14 36 -14 #000000"),
            Child = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.Parse("#EDE6DA")),
                FontSize = 13,
                MaxWidth = 360,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };

        _life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _life.Tick += (_, _) => Dismiss();
    }

    /// <summary>Create and show a toast (call on the UI thread).</summary>
    public static void Show(string message)
    {
        var toast = new ToastWindow(message);
        toast.Show();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is not null)
        {
            var b = screen.WorkingArea;
            var wpx = (int)(Bounds.Width * screen.Scaling);
            var hpx = (int)(Bounds.Height * screen.Scaling);
            Position = new PixelPoint(
                b.X + (b.Width - wpx) / 2,
                b.Y + (int)(b.Height * 0.82) - hpx / 2);
        }

        _life.Start();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Dismiss();
    }

    private void Dismiss()
    {
        _life.Stop();
        Close();
    }
}
