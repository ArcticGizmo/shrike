using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Shrike.App.Views;

/// <summary>
/// Loads the embedded <c>CHANGELOG.md</c> and renders its (lightweight) markdown into a stacked column of
/// themed text. Shared by the About window and the post-update <see cref="ChangelogWindow"/> so the two
/// read identically. Handles just the subset the changelog uses: <c>## </c>/<c>### </c> headings,
/// <c>-</c>/<c>*</c> bullets, <c>&gt; </c> quotes, <c>---</c> rules, and inline emphasis/links.
/// </summary>
internal static class ChangelogView
{
    private static readonly IBrush Fg = new SolidColorBrush(Color.Parse("#D8CFBF"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F5A524"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9A8F7D"));
    private static readonly IBrush Rule = new SolidColorBrush(Color.Parse("#322A1E"));

    /// <summary>Reads the changelog embedded at build time (csproj LogicalName <c>CHANGELOG.md</c>), or null.</summary>
    public static string? LoadEmbedded()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
            if (s is null) return null;
            using var reader = new StreamReader(s);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    /// <summary>Appends one control per markdown line into <paramref name="page"/>.</summary>
    public static void Render(StackPanel page, IEnumerable<string> lines)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("## "))
                page.Children.Add(Heading(StripInline(line[3..]), 15, Amber, top: 10));
            else if (line.StartsWith("### "))
                page.Children.Add(Heading(StripInline(line[4..]), 13, Fg, top: 6));
            else if (line.StartsWith("# ")) { /* the H1 title is redundant here */ }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
                page.Children.Add(Body("•  " + StripInline(line[2..])));
            else if (line == "---")
                page.Children.Add(new Border { Height = 1, Background = Rule, Margin = new Thickness(0, 8) });
            else if (line.StartsWith("> "))
                page.Children.Add(new TextBlock
                {
                    Text = StripInline(line[2..]), TextWrapping = TextWrapping.Wrap, FontSize = 12,
                    FontStyle = FontStyle.Italic, Foreground = Muted, Margin = new Thickness(12, 0, 0, 6),
                });
            else if (line.Trim().Length > 0)
                page.Children.Add(Body(StripInline(line)));
        }
    }

    private static TextBlock Heading(string text, double size, IBrush brush, double top) => new()
    {
        Text = text, FontSize = size, FontWeight = FontWeight.SemiBold, Foreground = brush,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 4),
    };

    private static TextBlock Body(string text) => new()
    {
        Text = text, FontSize = 12.5, Foreground = Fg, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 2),
    };

    /// <summary>Strips inline markdown (bold/italic/code/links) down to its display text.</summary>
    public static string StripInline(string text)
    {
        text = Regex.Replace(text, @"\*\*(.*?)\*\*", "$1");
        text = Regex.Replace(text, @"__(.*?)__", "$1");
        text = Regex.Replace(text, @"\*(.*?)\*", "$1");
        text = Regex.Replace(text, @"_(.*?)_", "$1");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return text;
    }
}
