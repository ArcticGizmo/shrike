using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Shrike.App.Native;
using Shrike.Core.Capture;

namespace Shrike.App.Views;

/// <summary>
/// The screenshot self-timer: a small "capturing in N…" pill that counts down over the target monitor,
/// then raises <see cref="Elapsed"/>. Built in code like <see cref="ColorResultWindow"/>.
///
/// Crucially it is <b>excluded from capture</b> (via <see cref="WindowExclusion"/>) so it shows on screen
/// while you arrange transient UI (a menu, a hover tooltip) but never lands in the shot — and it's
/// click-through and non-activating so it never steals focus or blocks the app underneath.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DelayCountdownWindow : Window
{
    private readonly int _seconds;
    private readonly MonitorInfo _monitor;
    private readonly TextBlock _count;
    private DispatcherTimer? _timer;
    private int _remaining;
    private bool _fired;

    /// <summary>Raised on the UI thread when the countdown hits zero.</summary>
    public event Action? Elapsed;

    // Parameterless ctor for the XAML designer only.
    public DelayCountdownWindow() : this(3, new MonitorInfo(new PixelBounds(0, 0, 1920, 1080), 1.0, true)) { }

    internal DelayCountdownWindow(int seconds, MonitorInfo monitor)
    {
        _seconds = Math.Max(1, seconds);
        _remaining = _seconds;
        _monitor = monitor;

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;   // never grab focus — the user is arranging another window
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Shrike Timer";

        _count = new TextBlock
        {
            Text = _remaining.ToString(),
            FontFamily = new FontFamily("Consolas,monospace"),
            FontSize = 40,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("#F5A524"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        Content = new Border
        {
            Background = Brush("#F214110D"),
            BorderBrush = Brush("#F5A524"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20, 12),
            BoxShadow = BoxShadows.Parse("0 18 44 -18 #000000"),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    _count,
                    new TextBlock
                    {
                        Text = "capturing…",
                        FontFamily = new FontFamily("Consolas,monospace"),
                        FontSize = 11,
                        LetterSpacing = 2,
                        Foreground = Brush("#8A7C68"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        };
    }

    /// <summary>Show the pill and start ticking. Call on the UI thread.</summary>
    internal void Start()
    {
        Show();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0)
            {
                _timer!.Stop();
                Fire();
            }
            else
            {
                _count.Text = _remaining.ToString();
            }
        };
        _timer.Start();
    }

    private void Fire()
    {
        if (_fired) return;
        _fired = true;
        Elapsed?.Invoke();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Top-centre of the target monitor, nudged down a little from the edge.
        var b = _monitor.Bounds;
        var wpx = (int)(Bounds.Width * _monitor.Scale);
        var x = b.X + Math.Max(0, (b.Width - wpx) / 2);
        var y = b.Y + (int)(64 * _monitor.Scale);
        Position = new PixelPoint(x, y);

        if (OperatingSystem.IsWindows())
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            WindowExclusion.Hide(hwnd);             // visible on screen, but never in the capture
            WindowExclusion.MakeClickThrough(hwnd); // clicks pass through to the app being captured
            WindowExclusion.MakeNonActivating(hwnd);// never steals focus from that app
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        base.OnClosed(e);
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
