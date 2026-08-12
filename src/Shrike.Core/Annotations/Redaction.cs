using Shrike.Core.Capture;

namespace Shrike.Core.Annotations;

/// <summary>
/// Applies <b>true, destructive</b> redaction: the covered pixels are overwritten with an opaque
/// fill in the output buffer, so a redacted export carries no recoverable trace of the original —
/// unlike a blur or a movable overlay. This is the security guarantee behind the redaction tool, and
/// it is deliberately a pure, unit-testable function applied as the final step of export.
/// </summary>
public static class Redaction
{
    /// <summary>Default redaction fill — solid black.</summary>
    public static readonly (byte R, byte G, byte B) DefaultFill = (0, 0, 0);

    /// <summary>
    /// Return a copy of <paramref name="image"/> with every rect (image-pixel coordinates) filled
    /// opaque. Rects are clamped to the image; out-of-bounds rects are ignored.
    /// </summary>
    public static CapturedImage Apply(CapturedImage image, IEnumerable<PixelBounds> rects, (byte R, byte G, byte B)? fill = null)
    {
        var (fr, fg, fb) = fill ?? DefaultFill;
        var buffer = (byte[])image.Bgra.Clone();
        var bounds = new PixelBounds(0, 0, image.Width, image.Height);

        foreach (var rect in rects)
        {
            var r = rect.Normalized().Intersect(bounds);
            if (r.IsEmpty) continue;

            for (var y = r.Y; y < r.Bottom; y++)
            {
                var rowStart = (y * image.Width + r.X) * 4;
                for (var x = 0; x < r.Width; x++)
                {
                    var i = rowStart + x * 4;
                    buffer[i + 0] = fb; // B
                    buffer[i + 1] = fg; // G
                    buffer[i + 2] = fr; // R
                    buffer[i + 3] = 255; // A (opaque)
                }
            }
        }

        return new CapturedImage(image.Width, image.Height, buffer, image.Source, image.CapturedAt);
    }
}
