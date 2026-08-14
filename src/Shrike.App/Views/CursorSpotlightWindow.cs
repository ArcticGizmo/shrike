using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using Shrike.App.Native;

namespace Shrike.App.Views;

/// <summary>The look of the spotlight glow: a hex colour, a core opacity (0..1) and a radius in px.</summary>
internal readonly record struct SpotlightStyle(string Color, double Opacity, int Radius);

/// <summary>
/// A soft glow that follows the mouse — the "spotlight cursor". Unlike the HUD and the region frame, this
/// overlay is deliberately <b>not</b> excluded from capture: it's a real, click-through, topmost layered
/// window that the GDI recorder grabs along with everything else (its <c>CAPTUREBLT</c> already includes
/// layered windows). So the spotlight shows on the user's screen <i>and</i> lands in the recording from a
/// single source of truth — the user sees exactly what's captured. A fast timer re-centres the window on
/// the cursor.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CursorSpotlightWindow : Window
{
    private readonly DispatcherTimer _follow;
    private readonly Ellipse _glow = new() { IsHitTestVisible = false };
    private SpotlightStyle _style;

    // Parameterless ctor for the XAML designer only.
    public CursorSpotlightWindow() : this(new SpotlightStyle("#FFD24A", 0.55, 48)) { }

    internal CursorSpotlightWindow(SpotlightStyle style)
    {
        _style = style;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;      // never steal focus from the app being recorded
        CanResize = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = _glow;
        ApplyStyle();

        _follow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _follow.Tick += (_, _) => FollowCursor();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (OperatingSystem.IsWindows())
            WindowExclusion.MakeClickThrough(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        FollowCursor();
    }

    /// <summary>Show/hide the spotlight and start/stop it tracking the cursor.</summary>
    public void SetActive(bool on)
    {
        if (on)
        {
            if (!IsVisible) Show();
            FollowCursor();
            _follow.Start();
        }
        else
        {
            _follow.Stop();
            if (IsVisible) Hide();
        }
    }

    /// <summary>Restyle the glow live (colour / opacity / size) from the HUD's settings flyout.</summary>
    internal void UpdateStyle(SpotlightStyle style)
    {
        _style = style;
        ApplyStyle();
        FollowCursor();
    }

    private void ApplyStyle()
    {
        var d = _style.Radius * 2;
        Width = d;
        Height = d;
        _glow.Width = d;
        _glow.Height = d;

        var c = Color.Parse(_style.Color);
        var coreA = (byte)Math.Clamp((int)Math.Round(_style.Opacity * 255), 0, 255);
        _glow.Fill = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(new Color(coreA, c.R, c.G, c.B), 0),
                new GradientStop(new Color((byte)(coreA * 0.45), c.R, c.G, c.B), 0.55),
                new GradientStop(new Color(0, c.R, c.G, c.B), 1),
            },
        };
    }

    private void FollowCursor()
    {
        var (cx, cy) = CursorPosition.Get();
        var screen = Screens.ScreenFromPoint(new PixelPoint(cx, cy)) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        var scale = screen?.Scaling ?? 1.0;
        var half = (int)Math.Round(Width * scale / 2); // Width is DIP; centre in physical pixels
        Position = new PixelPoint(cx - half, cy - half);
    }
}
