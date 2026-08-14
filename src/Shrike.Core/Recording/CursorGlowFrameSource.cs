using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Shrike.Core.Capture;

namespace Shrike.Core.Recording;

/// <summary>
/// Wraps another <see cref="IFrameSource"/> and, when <see cref="Enabled"/>, paints a soft glowing
/// halo under the mouse pointer so it's easy to follow in the finished video. The glow is composited
/// into the captured BGRA frame in region-local coordinates, so it lands in the recording exactly where
/// the cursor is. The recording HUD flips <see cref="Enabled"/> live, so the decorator is transparent
/// (returns the inner frame untouched) until the user asks for it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CursorGlowFrameSource : IFrameSource
{
    private readonly IFrameSource _inner;
    private readonly int _originX;
    private readonly int _originY;

    private const int Radius = 46;                       // halo reach, physical px
    private const double GlowB = 90, GlowG = 200, GlowR = 255; // warm amber (BGR), added as light

    public int Width => _inner.Width;
    public int Height => _inner.Height;

    /// <summary>Draw the glow into each frame when true; pass frames straight through when false.</summary>
    public volatile bool Enabled;

    public CursorGlowFrameSource(IFrameSource inner, PixelBounds region, bool enabled = false)
    {
        _inner = inner;
        var r = region.Normalized();
        _originX = r.X;
        _originY = r.Y;
        Enabled = enabled;
    }

    public byte[] CaptureFrame()
    {
        var frame = _inner.CaptureFrame();
        if (Enabled && GetCursorPos(out var p))
            Paint(frame, p.X - _originX, p.Y - _originY);
        return frame;
    }

    /// <summary>Blend a radial glow centred at frame-local (cx, cy) into the top-down BGRA buffer.</summary>
    private void Paint(byte[] bgra, int cx, int cy)
    {
        int w = Width, h = Height;
        var x0 = Math.Max(0, cx - Radius);
        var y0 = Math.Max(0, cy - Radius);
        var x1 = Math.Min(w - 1, cx + Radius);
        var y1 = Math.Min(h - 1, cy + Radius);
        if (x0 > x1 || y0 > y1) return; // cursor's halo doesn't reach the frame

        // Gaussian falloff that fades to near-zero by Radius.
        var twoSigmaSq = 2.0 * (Radius / 2.0) * (Radius / 2.0);
        var rSq = Radius * Radius;
        for (var y = y0; y <= y1; y++)
        {
            var row = y * w * 4;
            for (var x = x0; x <= x1; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var d2 = dx * dx + dy * dy;
                if (d2 > rSq) continue;
                var a = Math.Exp(-d2 / twoSigmaSq); // 0..1
                var i = row + x * 4;
                bgra[i] = Add(bgra[i], GlowB * a);
                bgra[i + 1] = Add(bgra[i + 1], GlowG * a);
                bgra[i + 2] = Add(bgra[i + 2], GlowR * a);
            }
        }
    }

    private static byte Add(byte channel, double light) => (byte)Math.Min(255.0, channel + light);

    public void Dispose() => _inner.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
