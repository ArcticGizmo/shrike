using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Shrike.Core.Recording;

namespace Shrike.App.Controls;

/// <summary>
/// A read-only waveform lane under the scrubber, sharing its <b>source-time</b> x-axis so the audio lines up
/// with the video above it. Draws a precomputed peak array (see <c>Waveform.ComputePeaks</c>) as a symmetric
/// waveform, dims cut spans like the effects lane, and tracks the playhead. Ctrl/Shift+wheel zoom/pan the
/// shared timeline view. Purely a view — it never edits the audio (gain/mute live in the properties pane).
/// </summary>
public sealed class WaveformLane : Control
{
    public Timeline? Timeline { get; set; }

    private float[] _peaks = [];
    private long _audioDurationMs;

    public long PlayheadMs { get; private set; }
    public long ViewStartMs { get; private set; }
    public long ViewEndMs { get; private set; }

    public WaveformLane()
    {
        Focusable = false;
        Height = 40;
    }

    /// <summary>Ctrl+wheel: zoom the view around this source ms. Shift+wheel: pan by this wheel delta.</summary>
    public event Action<long, double>? ZoomRequested;
    public event Action<double>? PanRequested;

    /// <summary>Raised when the lane is clicked, with the source ms under the pointer — selects the audio clip.</summary>
    public event Action<long>? Clicked;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Bounds.Width <= 0) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            Clicked?.Invoke(MsAt(e.GetPosition(this).X));
    }

    /// <summary>Set the decimated peaks (0..1) covering [0, <paramref name="audioDurationMs"/>] of the sidecar.</summary>
    public void SetWaveform(float[] peaks, long audioDurationMs)
    {
        _peaks = peaks ?? [];
        _audioDurationMs = audioDurationMs;
        InvalidateVisual();
    }

    public void SetView(long startMs, long endMs) { ViewStartMs = startMs; ViewEndMs = endMs; InvalidateVisual(); }
    public void SetPlayhead(long sourceMs) { PlayheadMs = sourceMs; InvalidateVisual(); }

    // ---- axis helpers (identical mapping to the other lanes) ----
    private double Dur => Timeline is { DurationMs: > 0 } tl ? tl.DurationMs : 1;
    private double ViewSpan => ViewEndMs > ViewStartMs ? ViewEndMs - ViewStartMs : Dur;
    private double X(long ms) => (ms - ViewStartMs) / ViewSpan * Bounds.Width;
    private long MsAt(double x) => (long)Math.Clamp(ViewStartMs + x / Math.Max(1, Bounds.Width) * ViewSpan, 0, Dur);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Timeline is null || Bounds.Width <= 0) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) { ZoomRequested?.Invoke(MsAt(e.GetPosition(this).X), e.Delta.Y); e.Handled = true; }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { PanRequested?.Invoke(e.Delta.Y); e.Handled = true; }
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (Timeline is null || w <= 0) return;

        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#120E08")), new Rect(0, 0, w, h));

        var mid = h / 2;
        if (_peaks.Length > 0 && _audioDurationMs > 0)
        {
            var fill = new SolidColorBrush(Color.Parse("#3E8E84")); // teal waveform
            var maxH = (h - 6) / 2;
            for (var x = 0; x < w; x++)
            {
                var srcMs = MsAt(x + 0.5);
                if (srcMs < 0 || srcMs > _audioDurationMs) continue;
                var idx = (int)Math.Clamp((double)srcMs / _audioDurationMs * (_peaks.Length - 1), 0, _peaks.Length - 1);
                var bh = Math.Max(0.5, _peaks[idx] * maxH);
                ctx.FillRectangle(fill, new Rect(x, mid - bh, 1, bh * 2));
            }
        }
        else
        {
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#2A2318")), 1), new Point(0, mid), new Point(w, mid));
        }

        // Dim cut spans so the lane reads against the scrubber above it.
        var dim = new SolidColorBrush(Color.Parse("#80140F0A"));
        foreach (var s in Timeline.Segments)
            if (!s.Kept) ctx.FillRectangle(dim, new Rect(X(s.StartMs), 0, Math.Max(1, X(s.EndMs) - X(s.StartMs)), h));

        // Playhead.
        var px = X(PlayheadMs);
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#F5A524")), 1), new Point(px, 0), new Point(px, h));

        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#322A1E"))), new Rect(0.5, 0.5, w - 1, h - 1));
    }
}
