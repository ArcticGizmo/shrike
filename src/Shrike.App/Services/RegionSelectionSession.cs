using Shrike.Core.Capture;

namespace Shrike.App.Services;

/// <summary>
/// Shared state for a region drag that may span several monitors. Each per-monitor overlay reports
/// pointer positions in <b>physical pixels</b> and renders from this one source of truth, so a
/// selection started on one screen and finished on another stays coherent. State is physical-pixel
/// only — no DIP/DPI concerns leak in here.
/// </summary>
internal sealed class RegionSelectionSession
{
    private (int X, int Y)? _start;
    private bool _finished;

    /// <summary>The current selection in physical pixels, or null before a drag begins.</summary>
    public PixelBounds? Current { get; private set; }

    public bool IsDragging => _start is not null && !_finished;

    /// <summary>Raised whenever <see cref="Current"/> changes so overlays can re-render.</summary>
    public event Action? Changed;

    /// <summary>Raised once with the final region (physical pixels) when the drag completes.</summary>
    public event Action<PixelBounds>? Completed;

    /// <summary>Raised once if the user cancels.</summary>
    public event Action? Cancelled;

    public void Begin(int physicalX, int physicalY)
    {
        if (_finished) return;
        _start = (physicalX, physicalY);
        Current = new PixelBounds(physicalX, physicalY, 0, 0);
        Changed?.Invoke();
    }

    public void Update(int physicalX, int physicalY)
    {
        if (_finished || _start is not { } start) return;
        Current = PixelBounds.FromCorners(start.X, start.Y, physicalX, physicalY);
        Changed?.Invoke();
    }

    public void Complete(int physicalX, int physicalY)
    {
        if (_finished) return;

        if (_start is { } start)
        {
            var region = PixelBounds.FromCorners(start.X, start.Y, physicalX, physicalY);
            if (region.Width >= 2 && region.Height >= 2)
            {
                _finished = true;
                Completed?.Invoke(region);
                return;
            }
        }

        Cancel(); // a click or sliver cancels rather than capturing nothing useful
    }

    public void Cancel()
    {
        if (_finished) return;
        _finished = true;
        Cancelled?.Invoke();
    }
}
