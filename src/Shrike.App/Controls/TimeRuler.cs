using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Shrike.Core.Recording;

namespace Shrike.App.Controls;

/// <summary>
/// A thin time axis above the scrubber, sharing the same source-time x-axis (0..<see cref="Timeline"/>.DurationMs).
/// Ticks land on a "nice" round interval — 0.1s, 0.5s, 1s, 5s, 10s, 30s, 1m, … — picked so the labels stay
/// readable (≥ a minimum pixel spacing) for the clip's length and the control's current width. Pure view.
/// </summary>
public sealed class TimeRuler : Control
{
    // Round intervals to snap ticks to (ms). Ascending; the smallest one that stays readable wins.
    private static readonly long[] Steps =
        [100, 500, 1000, 5000, 10000, 30000, 60000, 300000, 600000, 1_800_000, 3_600_000];

    private const double MinLabelPx = 64;

    public Timeline? Timeline { get; set; }

    public TimeRuler()
    {
        Height = 20;
        Focusable = false;
        ClipToBounds = true;
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (Timeline is not { DurationMs: > 0 } tl || w <= 0) return;
        double dur = tl.DurationMs;

        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#120E08")), new Rect(0, 0, w, h));

        // Smallest round interval whose on-screen spacing clears the minimum — the most ticks that still read.
        var step = Steps[^1];
        foreach (var s in Steps)
            if (s / dur * w >= MinLabelPx) { step = s; break; }

        var tickPen = new Pen(new SolidColorBrush(Color.Parse("#4A3E2C")));
        var textBrush = new SolidColorBrush(Color.Parse("#8B7E68"));
        for (long t = 0; t <= dur; t += step)
        {
            var x = t / dur * w;
            ctx.DrawLine(tickPen, new Point(x, h - 6), new Point(x, h));
            var ft = new FormattedText(Label(t, step), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Consolas"), 10.5, textBrush);
            ctx.DrawText(ft, new Point(Math.Min(x + 3, w - ft.Width - 1), 1));
        }

        ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#322A1E"))), new Point(0, h - 0.5), new Point(w, h - 0.5));
    }

    // Absolute time at a tick, formatted to match the chosen interval's granularity.
    private static string Label(long ms, long step)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (ms == 0) return "0";
        if (step >= 60000) return (ms / 60000) + "m";
        if (ms >= 60000) return $"{ms / 60000}:{(ms % 60000) / 1000:00}";
        if (step < 1000) return (ms / 1000.0).ToString("0.0", inv) + "s";
        return (ms / 1000) + "s";
    }
}
