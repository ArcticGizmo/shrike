using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shrike.Core.Annotations;

/// <summary>
/// Serialises a list of <see cref="Annotation"/>s to/from a compact, forgiving JSON DTO — used to persist a
/// canvas effect's drawing inside the clip's edit document. A per-item <c>T</c> discriminator selects the
/// concrete type; unknown or malformed items are dropped rather than failing the whole load, so a clip always
/// opens. Coordinates are image pixels (as in the model), so the drawing is resolution-independent.
/// </summary>
public static class AnnotationJson
{
    public static IReadOnlyList<AnnotationDto> ToDtos(IReadOnlyList<Annotation> items)
        => items.Select(ToDto).ToArray();

    public static List<Annotation> FromDtos(IEnumerable<AnnotationDto>? dtos)
    {
        var list = new List<Annotation>();
        if (dtos is null) return list;
        foreach (var d in dtos)
        {
            var a = FromDto(d);
            if (a is not null) list.Add(a);
        }
        return list;
    }

    private static AnnotationDto ToDto(Annotation a)
    {
        var dto = new AnnotationDto { Color = a.Color, Stroke = a.StrokeWidth, Rot = a.Rotation };
        switch (a)
        {
            case RectAnnotation r: dto.T = "rect"; dto.X = r.X; dto.Y = r.Y; dto.W = r.Width; dto.H = r.Height; dto.Filled = r.Filled; break;
            case EllipseAnnotation e: dto.T = "ellipse"; dto.X = e.X; dto.Y = e.Y; dto.W = e.Width; dto.H = e.Height; dto.Filled = e.Filled; break;
            case LineAnnotation l: dto.T = "line"; dto.X = l.X1; dto.Y = l.Y1; dto.X2 = l.X2; dto.Y2 = l.Y2; dto.Arrow = l.Arrow; break;
            case FreehandAnnotation f: dto.T = "freehand"; dto.Points = Flatten(f.Points); break;
            case HighlightAnnotation h: dto.T = "highlight"; dto.X = h.X; dto.Y = h.Y; dto.W = h.Width; dto.H = h.Height; break;
            case RedactionAnnotation rd: dto.T = "redaction"; dto.X = rd.X; dto.Y = rd.Y; dto.W = rd.Width; dto.H = rd.Height; break;
            case TextAnnotation t: dto.T = "text"; dto.X = t.X; dto.Y = t.Y; dto.Text = t.Text; dto.FontSize = t.FontSize; break;
            case StepBadgeAnnotation b: dto.T = "badge"; dto.X = b.X; dto.Y = b.Y; dto.Number = b.Number; break;
            default: dto.T = "?"; break;
        }
        return dto;
    }

    private static Annotation? FromDto(AnnotationDto d)
    {
        Annotation? a = d.T switch
        {
            "rect" => new RectAnnotation(d.X, d.Y, d.W, d.H, d.Filled),
            "ellipse" => new EllipseAnnotation(d.X, d.Y, d.W, d.H, d.Filled),
            "line" => new LineAnnotation(d.X, d.Y, d.X2, d.Y2, d.Arrow),
            "freehand" => new FreehandAnnotation(Unflatten(d.Points)),
            "highlight" => new HighlightAnnotation(d.X, d.Y, d.W, d.H),
            "redaction" => new RedactionAnnotation(d.X, d.Y, d.W, d.H),
            "text" => new TextAnnotation(d.X, d.Y, d.Text ?? "", d.FontSize <= 0 ? 18 : d.FontSize),
            "badge" => new StepBadgeAnnotation(d.X, d.Y, d.Number),
            _ => null,
        };
        if (a is null) return null;
        return a with
        {
            Color = string.IsNullOrWhiteSpace(d.Color) ? "#F5A524" : d.Color,
            StrokeWidth = d.Stroke <= 0 ? 3 : d.Stroke,
            Rotation = d.Rot,
        };
    }

    private static double[] Flatten(IReadOnlyList<PointD> pts)
    {
        var a = new double[pts.Count * 2];
        for (var i = 0; i < pts.Count; i++) { a[i * 2] = pts[i].X; a[i * 2 + 1] = pts[i].Y; }
        return a;
    }

    private static IReadOnlyList<PointD> Unflatten(double[]? flat)
    {
        var pts = new List<PointD>();
        if (flat is null) return pts;
        for (var i = 0; i + 1 < flat.Length; i += 2) pts.Add(new PointD(flat[i], flat[i + 1]));
        return pts;
    }
}

/// <summary>The JSON shape of one annotation — a discriminator plus the union of every type's fields (only the
/// relevant ones are written, thanks to null-ignoring serialisation).</summary>
public sealed class AnnotationDto
{
    public string T { get; set; } = "?";
    public string Color { get; set; } = "#F5A524";
    public double Stroke { get; set; } = 3;
    public double Rot { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public bool Filled { get; set; }
    public bool Arrow { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Points { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }
    public double FontSize { get; set; }
    public int Number { get; set; }
}
