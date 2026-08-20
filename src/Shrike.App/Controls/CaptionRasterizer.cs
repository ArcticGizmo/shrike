using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Shrike.Core.Recording;

namespace Shrike.App.Controls;

/// <summary>
/// Bakes one caption line into a <see cref="CaptionSprite"/> (premultiplied BGRA + on-frame position) for the
/// burn-in compositor. Renders a styled, word-wrapped text block over a translucent rounded box via Avalonia
/// (a <see cref="RenderTargetBitmap"/>), so the exported caption matches the preview's look. Sizes derive from
/// the export frame height, so text reads consistently at any resolution. Must run on the UI thread.
/// </summary>
public static class CaptionRasterizer
{
    /// <summary>Render <paramref name="text"/> in <paramref name="style"/> for a <paramref name="width"/> ×
    /// <paramref name="height"/> export frame. Null for empty text.</summary>
    public static CaptionSprite? Render(string text, CaptionStyle style, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(text) || width <= 0 || height <= 0) return null;

        var fontPx = Math.Clamp(height * 0.042 * style.FontScale, 12, height * 0.2);
        var padX = fontPx * 0.6;
        var padY = fontPx * 0.32;
        var radius = fontPx * 0.25;
        var margin = height * 0.055;
        var maxTextWidth = Math.Max(40, width * Math.Clamp(style.MaxWidthFraction, 0.2, 1.0) - 2 * padX);

        var boxColor = ParseColor(style.BoxColor, Colors.Black);
        boxColor = new Color((byte)(Math.Clamp(style.BoxOpacity, 0, 1) * 255), boxColor.R, boxColor.G, boxColor.B);
        var textColor = ParseColor(style.TextColor, Colors.White);

        var box = new Border
        {
            Background = new SolidColorBrush(boxColor),
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(padX, padY),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = text.Trim(),
                Foreground = new SolidColorBrush(textColor),
                FontSize = fontPx,
                FontWeight = FontWeight.SemiBold,
                FontFamily = FontFamily.Default,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = maxTextWidth,
            },
        };
        // Grayscale antialiasing — ClearType subpixel rendering leaves coloured fringes on the glyph edges
        // once the sprite is composited over the video. The attached property inherits to the TextBlock child.
        TextOptions.SetTextRenderingMode(box, TextRenderingMode.Antialias);

        // Measure to the natural (wrapped) size, then render exactly that big.
        box.Measure(new Size(maxTextWidth + 2 * padX, double.PositiveInfinity));
        var desired = box.DesiredSize;
        int w = (int)Math.Ceiling(desired.Width);
        int h = (int)Math.Ceiling(desired.Height);
        if (w <= 0 || h <= 0) return null;
        box.Arrange(new Rect(0, 0, w, h));

        var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        rtb.Render(box);

        var buffer = new byte[w * h * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { rtb.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buffer.Length, w * 4); }
        finally { handle.Free(); }

        // Centre horizontally; sit in the lower (or upper) third with a margin, clamped into the frame.
        int x = Math.Max(0, (width - w) / 2);
        int y = style.Position == CaptionPosition.Top
            ? (int)Math.Round(margin)
            : (int)Math.Round(height - h - margin);
        y = Math.Clamp(y, 0, Math.Max(0, height - h));

        return new CaptionSprite(buffer, w, h, x, y);
    }

    private static Color ParseColor(string? hex, Color fallback)
        => !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var c) ? c : fallback;
}
