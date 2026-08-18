using Shrike.Core.Capture;

namespace Shrike.Core.Recording;

/// <summary>
/// Accumulates a <see cref="MouseTrack"/> live during a recording. Each event is stamped with the
/// recording's own pause-excluded clock via <c>captureTimeMs</c>: when that returns null (the recording
/// is paused, stopped, or not yet started) the event is dropped, so the track shares the video's timeline
/// exactly and carries no samples from paused spans. Append is cheap and thread-safe — the low-level
/// mouse hook fires on the UI thread on every move, so the callback must stay light.
/// </summary>
public sealed class MouseTrackRecorder
{
    private readonly PixelBounds _region;
    private readonly Func<long?> _captureTimeMs;
    private readonly List<MousePoint> _points = [];
    private readonly List<MouseClick> _clicks = [];
    private readonly object _lock = new();

    /// <param name="region">The recorded rectangle in virtual-screen physical pixels.</param>
    /// <param name="captureTimeMs">Returns the current recording position in ms (pause-excluded), or null
    /// when not actively recording — see <c>Recorder.CaptureTimeMs</c>.</param>
    public MouseTrackRecorder(PixelBounds region, Func<long?> captureTimeMs)
    {
        _region = region;
        _captureTimeMs = captureTimeMs ?? throw new ArgumentNullException(nameof(captureTimeMs));
    }

    /// <summary>Record a pointer position (virtual-screen physical px). Dropped if not currently recording.</summary>
    public void Move(int x, int y)
    {
        if (_captureTimeMs() is not { } t) return;
        lock (_lock) _points.Add(new MousePoint((int)t, x, y));
    }

    /// <summary>Record a button transition. Dropped if not currently recording.</summary>
    public void Click(MouseButtonKind button, bool down)
    {
        if (_captureTimeMs() is not { } t) return;
        lock (_lock) _clicks.Add(new MouseClick((int)t, button, down));
    }

    /// <summary>Snapshot everything captured so far into an immutable <see cref="MouseTrack"/>.</summary>
    public MouseTrack Build()
    {
        lock (_lock) return new MouseTrack(_region, _points.ToArray(), _clicks.ToArray());
    }

    /// <summary>Number of points captured so far (for diagnostics/tests).</summary>
    public int PointCount { get { lock (_lock) return _points.Count; } }
}
