using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Shrike.App.Controls;

/// <summary>
/// Draws the current preview frame, Uniform-fit and centred. Used for both the crisp still shown while
/// paused/scrubbing and the live frames during playback. Unlike an <see cref="Image"/>, it repaints
/// whenever <see cref="Show"/> is called even if the bitmap reference is unchanged — which is exactly
/// what streaming playback needs, since it updates the pixels of one reused WriteableBitmap each frame.
/// </summary>
public sealed class PreviewSurface : Control
{
    private IImage? _image;

    /// <summary>Set (or refresh) the frame to display and repaint immediately.</summary>
    public void Show(IImage image)
    {
        _image = image;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var img = _image;
        if (img is null) return;

        var src = img.Size;
        if (src.Width <= 0 || src.Height <= 0) return;

        var dst = Bounds.Size;
        var scale = Math.Min(dst.Width / src.Width, dst.Height / src.Height);
        var w = src.Width * scale;
        var h = src.Height * scale;
        var rect = new Rect((dst.Width - w) / 2, (dst.Height - h) / 2, w, h);
        ctx.DrawImage(img, new Rect(src), rect);
    }
}
