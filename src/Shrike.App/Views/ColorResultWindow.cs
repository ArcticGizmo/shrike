using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Shrike.App.Native;
using Shrike.Core.Capture;

namespace Shrike.App.Views;

/// <summary>
/// The pipette result: a small panel showing the picked colour as a swatch plus its HEX / RGB / HSL
/// forms. Clicking a row copies that form to the clipboard and toasts; Esc (or clicking away) closes.
/// Built in code, like <see cref="ToastWindow"/>, so it needs no XAML/styles wired up.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ColorResultWindow : Window
{
    private readonly PixelPoint _anchor;

    // Parameterless ctor for the XAML designer only.
    public ColorResultWindow() : this(new PixelColor(58, 123, 213), new PixelPoint(0, 0)) { }

    internal ColorResultWindow(PixelColor color, PixelPoint anchor)
    {
        _anchor = anchor;

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Shrike Colour";

        var rows = new StackPanel { Spacing = 3 };
        rows.Children.Add(BuildHeader(color));
        rows.Children.Add(new Border { Height = 1, Background = Brush("#322A1E"), Margin = new Thickness(4, 4) });
        rows.Children.Add(BuildCopyRow("HEX", color.Hex));
        rows.Children.Add(BuildCopyRow("RGB", color.Rgb));
        rows.Children.Add(BuildCopyRow("HSL", color.Hsl));
        rows.Children.Add(new TextBlock
        {
            Text = "Click a value to copy  ·  Esc to close",
            FontSize = 11,
            Foreground = Brush("#8A7C68"),
            Margin = new Thickness(6, 5, 0, 2),
        });

        Content = new Border
        {
            Background = Brush("#F214110D"),
            BorderBrush = Brush("#F5A524"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            BoxShadow = BoxShadows.Parse("0 18 44 -18 #000000"),
            Child = new StackPanel { Width = 250, Children = { rows } },
        };
    }

    private Control BuildHeader(PixelColor color)
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            Margin = new Thickness(6, 4, 0, 2),
            Children =
            {
                new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(5),
                    BorderBrush = Brush("#66000000"),
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "PICKED COLOUR",
                    FontFamily = new FontFamily("Consolas,monospace"),
                    FontSize = 11,
                    Foreground = Brush("#F5A524"),
                    LetterSpacing = 2,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
    }

    private Button BuildCopyRow(string label, string value)
    {
        var row = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 8),
            Background = Brush("#2A2318"),
            Foreground = Brush("#EDE6DA"),
            CornerRadius = new CornerRadius(8),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        FontFamily = new FontFamily("Consolas,monospace"),
                        FontSize = 12,
                        Foreground = Brush("#F5A524"),
                        Width = 34,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = value,
                        FontFamily = new FontFamily("Consolas,monospace"),
                        FontSize = 13,
                        Foreground = Brush("#EDE6DA"),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };
        row.Click += (_, _) => Copy(value);
        return row;
    }

    private void Copy(string value)
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var ok = ClipboardImage.SetText(hwnd, value);
        ToastWindow.Show(ok ? $"Copied {value}" : "Clipboard busy — try again");
        Close();
    }

    /// <summary>Create and show the result panel near a screen point (call on the UI thread).</summary>
    internal static void Show(PixelColor color, PixelPoint anchor)
        => new ColorResultWindow(color, anchor).Show();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Sit just off the anchor, nudged back onto the screen if we'd overflow its right/bottom edge.
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
        if (e.Key == Key.Escape)
            Close();
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
