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
}
