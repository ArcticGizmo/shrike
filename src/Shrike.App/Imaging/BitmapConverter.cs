using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Shrike.Core.Capture;

namespace Shrike.App.Imaging;

/// <summary>Bridges a <see cref="CapturedImage"/> (raw BGRA from Core) into an Avalonia bitmap for display,
/// copying row-by-row to respect the target's stride.</summary>
internal static class BitmapConverter
{
    public static WriteableBitmap ToBitmap(CapturedImage image)
    {
        var bmp = new WriteableBitmap(
            new PixelSize(image.Width, image.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using var fb = bmp.Lock();
        var srcStride = image.Width * 4;
        var dstStride = fb.RowBytes;

        for (var row = 0; row < image.Height; row++)
        {
            Marshal.Copy(
                image.Bgra,
                row * srcStride,
                fb.Address + row * dstStride,
                srcStride);
        }

        return bmp;
    }
}
