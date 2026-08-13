namespace Shrike.Core.Capture;

/// <summary>
/// Produces small preview copies of a <see cref="CapturedImage"/> for the recent-captures ring
/// surfaces (tray flyout icons, editor filmstrip). Box-averages source blocks so the result stays
/// readable when scaled down — a deliberately simple, headless-testable downscale (no UI toolkit).
/// </summary>
public static class Thumbnail
{
    /// <summary>
    /// Downscale <paramref name="src"/> so its longest side is at most <paramref name="maxDimension"/>
    /// pixels, preserving aspect ratio. Images already within the bound are returned unchanged (never
    /// upscaled). Each destination pixel is the average of the source block it covers.
    /// </summary>
    public static CapturedImage Downscale(CapturedImage src, int maxDimension = 160)
    {
        if (maxDimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDimension), "Thumbnail size must be positive.");

        var longest = Math.Max(src.Width, src.Height);
        if (longest <= maxDimension)
            return src;

        var scale = (double)maxDimension / longest;
        var dstW = Math.Max(1, (int)Math.Round(src.Width * scale));
        var dstH = Math.Max(1, (int)Math.Round(src.Height * scale));

        var srcStride = src.Width * 4;
        var dstStride = dstW * 4;
        var outBuffer = new byte[dstStride * dstH];

        for (var dy = 0; dy < dstH; dy++)
        {
            // Source rows [y0, y1) that map onto this destination row.
            var y0 = dy * src.Height / dstH;
            var y1 = Math.Max(y0 + 1, (dy + 1) * src.Height / dstH);

            for (var dx = 0; dx < dstW; dx++)
            {
                var x0 = dx * src.Width / dstW;
                var x1 = Math.Max(x0 + 1, (dx + 1) * src.Width / dstW);

                long b = 0, g = 0, r = 0, a = 0;
                var count = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var rowBase = sy * srcStride;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var i = rowBase + sx * 4;
                        b += src.Bgra[i + 0];
                        g += src.Bgra[i + 1];
                        r += src.Bgra[i + 2];
                        a += src.Bgra[i + 3];
                        count++;
                    }
                }

                var o = dy * dstStride + dx * 4;
                outBuffer[o + 0] = (byte)(b / count);
                outBuffer[o + 1] = (byte)(g / count);
                outBuffer[o + 2] = (byte)(r / count);
                outBuffer[o + 3] = (byte)(a / count);
            }
        }

        // Keep the source rectangle so a thumbnail still reports where it came from.
        return new CapturedImage(dstW, dstH, outBuffer, src.Source, src.CapturedAt);
    }
}
