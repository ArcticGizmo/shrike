namespace Shrike.Core.Capture;

/// <summary>
/// An integer rectangle in physical screen pixels (virtual-desktop coordinate space, so it can be
/// negative on secondary monitors left of / above the primary). UI-framework agnostic on purpose —
/// <c>Shrike.Core</c> never references Avalonia.
/// </summary>
public readonly record struct PixelBounds(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// <summary>Flip any negative width/height (e.g. a drag that went up-left) into a positive rect.</summary>
    public PixelBounds Normalized()
    {
        var x = Width < 0 ? X + Width : X;
        var y = Height < 0 ? Y + Height : Y;
        return new PixelBounds(x, y, Math.Abs(Width), Math.Abs(Height));
    }

    /// <summary>Build a rect from two opposite corners (as produced by a pointer drag).</summary>
    public static PixelBounds FromCorners(int x1, int y1, int x2, int y2)
        => new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    /// <summary>The overlap of two rects, or an empty rect if they don't intersect.</summary>
    public PixelBounds Intersect(PixelBounds other)
    {
        var a = Normalized();
        var b = other.Normalized();
        var x = Math.Max(a.X, b.X);
        var y = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return right <= x || bottom <= y
            ? default
            : new PixelBounds(x, y, right - x, bottom - y);
    }
}
