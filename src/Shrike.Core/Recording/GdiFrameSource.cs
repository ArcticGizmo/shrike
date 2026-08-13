using System.Runtime.Versioning;
using Shrike.Core.Capture;

namespace Shrike.Core.Recording;

/// <summary>
/// Captures a fixed screen region each frame with a GDI <c>BitBlt</c> (reusing <see cref="ScreenCapture"/>).
/// Simple and dependency-free; grabs whatever is composited on screen, including the cursor and layered
/// windows. The region is rounded to even dimensions so the H.264 (yuv420p) encoder accepts it.
/// Not as fast as GPU capture — a Windows.Graphics.Capture source is the intended upgrade behind
/// <see cref="IFrameSource"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GdiFrameSource : IFrameSource
{
    private readonly PixelBounds _region;

    public int Width => _region.Width;
    public int Height => _region.Height;

    public GdiFrameSource(PixelBounds region)
    {
        var r = region.Normalized();
        // yuv420p needs even width/height; trim a pixel rather than pad so we never sample past the region.
        var w = r.Width - (r.Width & 1);
        var h = r.Height - (r.Height & 1);
        if (w <= 0 || h <= 0)
            throw new ArgumentException("Recording region is too small.", nameof(region));
        _region = new PixelBounds(r.X, r.Y, w, h);
    }

    public byte[] CaptureFrame() => ScreenCapture.Capture(_region).Bgra;

    public void Dispose() { }
}
