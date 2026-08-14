using System.Globalization;

namespace Shrike.Core.Capture;

/// <summary>
/// A single sampled screen colour (opaque, 8-bit per channel), with the string forms a colour
/// pipette offers for copying: <c>#RRGGBB</c>, <c>rgb(r, g, b)</c>, and <c>hsl(h, s%, l%)</c>.
/// </summary>
public readonly record struct PixelColor(byte R, byte G, byte B)
{
    /// <summary>Uppercase hex, e.g. <c>#3A7BD5</c>.</summary>
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>CSS rgb form, e.g. <c>rgb(58, 123, 213)</c>.</summary>
    public string Rgb => string.Create(CultureInfo.InvariantCulture, $"rgb({R}, {G}, {B})");

    /// <summary>CSS hsl form with integer degrees and whole-percent saturation/lightness.</summary>
    public string Hsl
    {
        get
        {
            var (h, s, l) = ToHsl();
            return string.Create(CultureInfo.InvariantCulture,
                $"hsl({(int)Math.Round(h)}, {(int)Math.Round(s * 100)}%, {(int)Math.Round(l * 100)}%)");
        }
    }

    /// <summary>Convert to HSL: hue in [0,360), saturation and lightness in [0,1].</summary>
    public (double H, double S, double L) ToHsl()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;

        if (max == min)
            return (0, 0, l); // achromatic (grey)

        var d = max - min;
        var s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        double h;
        if (max == r)
            h = (g - b) / d + (g < b ? 6.0 : 0.0);
        else if (max == g)
            h = (b - r) / d + 2.0;
        else
            h = (r - g) / d + 4.0;
        h *= 60.0;

        return (h, s, l);
    }
}
