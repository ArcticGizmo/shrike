using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Shrike.App.Imaging;

/// <summary>Recolours an icon to a single hue (luminance-preserving), so the dev instance's tray icon is
/// visibly different from the installed Shrike without shipping a second asset.</summary>
internal static class IconTint
{
    // How much of the tint survives in the darkest pixels — keeps black outlines reading as dark pink
    // rather than staying pure black.
    private const double Floor = 0.35;

    public static Bitmap Tint(Bitmap source, Color tint)
    {
        var size = source.PixelSize;
        var stride = size.Width * 4;
        var px = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
        try { source.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), px.Length, stride); }
        finally { handle.Free(); }

        var format = source.Format ?? PixelFormat.Bgra8888;
        var alphaFormat = source.AlphaFormat ?? AlphaFormat.Premul;
        var rgba = format == PixelFormat.Rgba8888;
        var premul = alphaFormat == AlphaFormat.Premul;
        int ri = rgba ? 0 : 2, bi = rgba ? 2 : 0;

        for (var i = 0; i < px.Length; i += 4)
        {
            var a = px[i + 3];
            if (a == 0) continue;
            // Luma is linear, so it works on premultiplied channels too (it just comes out premultiplied).
            var luma = (0.299 * px[i + ri] + 0.587 * px[i + 1] + 0.114 * px[i + bi]) / 255.0;
            var level = Floor * (premul ? a / 255.0 : 1.0) + (1 - Floor) * luma;
            px[i + ri] = (byte)Math.Round(tint.R * level);
            px[i + 1] = (byte)Math.Round(tint.G * level);
            px[i + bi] = (byte)Math.Round(tint.B * level);
        }

        var result = new WriteableBitmap(size, source.Dpi, format, alphaFormat);
        using var fb = result.Lock();
        for (var row = 0; row < size.Height; row++)
            Marshal.Copy(px, row * stride, fb.Address + row * fb.RowBytes, stride);
        return result;
    }
}
