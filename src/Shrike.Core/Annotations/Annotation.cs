namespace Shrike.Core.Annotations;

/// <summary>A 2-D point in image-pixel coordinates (UI-framework agnostic).</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>An axis-aligned rectangle in image-pixel coordinates.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public bool Contains(PointD p) => p.X >= X && p.X <= X + Width && p.Y >= Y && p.Y <= Y + Height;

    /// <summary>Grow the rect by <paramref name="d"/> on every side.</summary>
    public RectD Inflate(double d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);
}

/// <summary>
/// Base for a non-destructive annotation. Coordinates are in <b>image pixels</b> (0..Width/Height of
/// the capture), so annotations are independent of how the editor is zoomed or displayed. Concrete
/// types are immutable records; editing replaces an item rather than mutating it.
/// </summary>
public abstract record Annotation
{
    /// <summary>Stroke/fill colour as <c>#RRGGBB</c>.</summary>
    public string Color { get; init; } = "#F5A524";

    /// <summary>Stroke width in image pixels.</summary>
    public double StrokeWidth { get; init; } = 3;
}

public sealed record RectAnnotation(double X, double Y, double Width, double Height, bool Filled = false) : Annotation;

public sealed record EllipseAnnotation(double X, double Y, double Width, double Height, bool Filled = false) : Annotation;

/// <summary>A straight line, optionally with an arrowhead at the (X2,Y2) end.</summary>
public sealed record LineAnnotation(double X1, double Y1, double X2, double Y2, bool Arrow = false) : Annotation;

public sealed record FreehandAnnotation(IReadOnlyList<PointD> Points) : Annotation;

/// <summary>A translucent highlighter swipe (rendered as a semi-transparent fill).</summary>
public sealed record HighlightAnnotation(double X, double Y, double Width, double Height) : Annotation;

/// <summary>
/// A region to be <b>destructively</b> removed on export — the underlying pixels are overwritten, not
/// merely covered (see <see cref="Redaction"/>). Security-sensitive: a redacted export must not leak
/// the original content.
/// </summary>
public sealed record RedactionAnnotation(double X, double Y, double Width, double Height) : Annotation;

public sealed record TextAnnotation(double X, double Y, string Text, double FontSize = 18) : Annotation;

/// <summary>A numbered step badge (1, 2, 3…) for call-outs.</summary>
public sealed record StepBadgeAnnotation(double X, double Y, int Number) : Annotation;
