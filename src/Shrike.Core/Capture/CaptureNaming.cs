using System.Text.RegularExpressions;

namespace Shrike.Core.Capture;

/// <summary>
/// Expands a filename template such as <c>shrike-{yyyyMMdd-HHmmss}</c>. The <c>{…}</c> token is a
/// .NET date/time format string applied to the capture time; text outside braces is literal. Kept
/// pure (time is passed in) so it unit-tests deterministically.
/// </summary>
public static partial class CaptureNaming
{
    /// <summary>The default template — matches the user's capture-naming habit.</summary>
    public const string DefaultTemplate = "shrike-{yyyyMMdd-HHmmss}";

    [GeneratedRegex(@"\{([^}]*)\}")]
    private static partial Regex TokenRegex();

    /// <summary>Expand a template to a bare filename (no extension), sanitised of invalid path chars.</summary>
    public static string Expand(string template, DateTimeOffset when)
    {
        if (string.IsNullOrWhiteSpace(template))
            template = DefaultTemplate;

        var expanded = TokenRegex().Replace(template, match =>
        {
            var format = match.Groups[1].Value;
            return string.IsNullOrEmpty(format)
                ? string.Empty
                : when.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        });

        return Sanitize(expanded);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim();
        return cleaned.Length == 0 ? "shrike" : cleaned;
    }
}
