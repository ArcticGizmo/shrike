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
///
/// Because it never has focus, Esc is grabbed as a global hotkey for the life of the countdown (and released
/// the moment it ends) so the user can abort from whatever app they're arranging — raising <see cref="Cancelled"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DelayCountdownWindow : Window
{
    private const int EscHotkeyId = 1;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_ESCAPE = 0x1B;

    // Each pill gets its own message window, so the class name must be unique (an old pill may not have
    // unregistered its class yet when a new one starts).
    private static int _instanceCounter;

    private readonly int _seconds;
    private readonly MonitorInfo _monitor;
    private readonly TextBlock _count;
    private DispatcherTimer? _timer;
    private MessageWindow? _escWindow;
    private int _remaining;
    private bool _fired;

    /// <summary>Raised on the UI thread when the countdown hits zero.</summary>
    public event Action? Elapsed;

    /// <summary>Raised on the UI thread when the user presses Esc during the countdown. The pill closes itself.</summary>
    public event Action? Cancelled;

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
                        Text = "capturing…  esc cancels",
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
        RegisterEscape();
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
        ReleaseEscape();   // hand Esc back before the region overlay (which wants it) opens
        Elapsed?.Invoke();
    }

    /// <summary>Grab Esc system-wide while counting down. Best-effort: if another app already owns a bare
    /// Esc hotkey the registration fails and the countdown simply can't be cancelled.</summary>
    private void RegisterEscape()
    {
        try
        {
            var window = new MessageWindow($"ShrikeDelayEsc{Interlocked.Increment(ref _instanceCounter)}");
            window.HotkeyPressed += id =>
            {
                if (id != EscHotkeyId || _fired) return;
                _fired = true;
                _timer?.Stop();
                ReleaseEscape();
                Close();
                Cancelled?.Invoke();
            };
            window.RegisterHotkey(EscHotkeyId, MOD_NOREPEAT, VK_ESCAPE);
            _escWindow = window;
        }
        catch (InvalidOperationException)
        {
            // Couldn't create the message window — the countdown still works, just without Esc.
        }
    }

    private void ReleaseEscape()
    {
        if (_escWindow is null) return;
        _escWindow.UnregisterHotkey(EscHotkeyId);
        _escWindow.Dispose();
        _escWindow = null;
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
        ReleaseEscape();
        base.OnClosed(e);
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
