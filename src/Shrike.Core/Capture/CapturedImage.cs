namespace Shrike.Core.Capture;

/// <summary>
/// A single captured still: straight (non-premultiplied) 32-bit BGRA pixels, top-down (row 0 is the
/// top), alpha forced opaque. This is the raw currency the encoder and clipboard helpers consume;
/// the App turns it into an Avalonia bitmap for display.
/// </summary>
public sealed class CapturedImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Top-down BGRA, length = Width * Height * 4.</summary>
    public byte[] Bgra { get; }

    public DateTimeOffset CapturedAt { get; }

    /// <summary>The screen rectangle (physical pixels) this image was taken from.</summary>
    public PixelBounds Source { get; }

    public CapturedImage(int width, int height, byte[] bgra, PixelBounds source, DateTimeOffset capturedAt)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Image dimensions must be positive.");
        if (bgra.Length != width * height * 4)
            throw new ArgumentException($"BGRA buffer is {bgra.Length} bytes; expected {width * height * 4}.");

        Width = width;
        Height = height;
        Bgra = bgra;
        Source = source;
        CapturedAt = capturedAt;
    }

    /// <summary>
    /// Extract a sub-image. <paramref name="region"/> is in the same physical-pixel space as
    /// <see cref="Source"/>; it is clamped to the image, and throws only if there is no overlap.
    /// Used to crop the final selection (and the magnifier's sample) from a frozen full-screen grab.
    /// </summary>
    public CapturedImage Crop(PixelBounds region)
    {
        var r = region.Normalized().Intersect(Source);
        if (r.IsEmpty)
            throw new ArgumentException("Crop region does not overlap the image.", nameof(region));

        var offsetX = r.X - Source.X;
        var offsetY = r.Y - Source.Y;
        var srcStride = Width * 4;
        var dstStride = r.Width * 4;
        var outBuffer = new byte[dstStride * r.Height];

        for (var row = 0; row < r.Height; row++)
        {
            Array.Copy(
                Bgra, (offsetY + row) * srcStride + offsetX * 4,
                outBuffer, row * dstStride,
                dstStride);
        }

        return new CapturedImage(r.Width, r.Height, outBuffer, r, CapturedAt);
    }
}
