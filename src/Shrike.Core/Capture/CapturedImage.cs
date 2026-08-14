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

    /// <summary>
    /// Read the colour of a single pixel. <paramref name="x"/>/<paramref name="y"/> are in the same
    /// physical-pixel space as <see cref="Source"/> (as reported by the overlay); the point is clamped
    /// to the image so a sample right at the edge still returns the nearest pixel. Used by the pipette.
    /// </summary>
    public PixelColor SampleColor(int x, int y)
    {
        var px = Math.Clamp(x - Source.X, 0, Width - 1);
        var py = Math.Clamp(y - Source.Y, 0, Height - 1);
        var off = (py * Width + px) * 4;
        // Buffer is BGRA, so red is at +2, green at +1, blue at +0.
        return new PixelColor(Bgra[off + 2], Bgra[off + 1], Bgra[off]);
    }
}
