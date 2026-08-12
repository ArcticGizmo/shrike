using System.Runtime.Versioning;
using Avalonia.Threading;
using Shrike.App.Native;
using Shrike.App.Views;
using Shrike.Core.Capture;
using Shrike.Core.Interop;

namespace Shrike.App.Services;

/// <summary>
/// Orchestrates a capture: pop a region overlay on every monitor → grab the pixels → show them in
/// the (reused) editor. Enforces the no-desktop-switch rule when surfacing the editor.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class CaptureController
{
    private readonly VirtualDesktopService _desktops;
    private readonly Action? _onOverlayShown;

    private readonly List<OverlayWindow> _overlays = [];
    private RegionSelectionSession? _session;
    private CapturedImage? _frozen;
    private EditorWindow? _editor;

    public CaptureController(VirtualDesktopService desktops, Action? onOverlayShown = null)
    {
        _desktops = desktops;
        _onOverlayShown = onOverlayShown;
    }

    /// <summary>Show a region-selection overlay on each monitor (or focus the existing set).</summary>
    public void BeginRegionCapture()
    {
        if (_overlays.Count > 0)
        {
            _overlays[0].Activate();
            return;
        }

        var monitors = Monitors.All();
        if (monitors.Count == 0)
            monitors = [new MonitorInfo(ScreenCapture.VirtualScreenBounds(), 1.0, true)];

        // Freeze the whole desktop once: the magnifier samples it and the final selection is cropped
        // from it, so what the loupe shows is exactly what gets captured.
        _frozen = TryCaptureFrozen();

        // Snapshot window rectangles BEFORE showing the overlays, so our overlays aren't in the list.
        var windows = TopLevelWindows.Enumerate();

        var session = new RegionSelectionSession();
        session.Completed += region =>
        {
            CloseOverlays();
            OnRegionSelected(region);
        };
        session.Cancelled += CloseOverlays;
        _session = session;

        foreach (var monitor in monitors)
        {
            var overlay = new OverlayWindow(session, monitor, _frozen, windows);
            _overlays.Add(overlay);
            overlay.Show();
        }

        _onOverlayShown?.Invoke();
    }

    private static CapturedImage? TryCaptureFrozen()
    {
        try
        {
            return ScreenCapture.Capture(ScreenCapture.VirtualScreenBounds());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Dismiss the overlays if open (used by the measure-startup path).</summary>
    public void CancelOverlay() => _session?.Cancel();

    /// <summary>Capture the whole (all-monitor) desktop straight to the editor.</summary>
    public void CaptureFullScreen() => CaptureAndEdit(ScreenCapture.VirtualScreenBounds());

    /// <summary>Capture the monitor the pointer is currently on.</summary>
    public void CaptureMonitorUnderCursor()
    {
        var (cx, cy) = CursorPosition.Get();
        var monitor = Monitors.All().FirstOrDefault(m =>
            cx >= m.Bounds.X && cx < m.Bounds.Right && cy >= m.Bounds.Y && cy < m.Bounds.Bottom);

        CaptureAndEdit(monitor.Bounds.IsEmpty ? ScreenCapture.VirtualScreenBounds() : monitor.Bounds);
    }

    /// <summary>Capture the current foreground window (its visible DWM frame).</summary>
    public void CaptureActiveWindow()
    {
        if (WindowBounds.TryForegroundWindow(out var bounds))
            CaptureAndEdit(bounds.Intersect(ScreenCapture.VirtualScreenBounds()));
    }

    /// <summary>Run a capture action after a delay (for menus/hover states). Zero delay runs now.</summary>
    public void RunAfter(TimeSpan delay, Action action)
    {
        if (delay <= TimeSpan.Zero)
        {
            action();
            return;
        }

        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    private void CaptureAndEdit(PixelBounds bounds)
    {
        if (bounds.IsEmpty)
            return;

        CapturedImage image;
        try
        {
            image = ScreenCapture.Capture(bounds);
        }
        catch
        {
            return;
        }

        ShowInEditor(image);
    }

    private void CloseOverlays()
    {
        // Defer so we never close a window from inside its own pointer/key event.
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var overlay in _overlays.ToArray())
                overlay.Close();
            _overlays.Clear();
            _session = null;
            _frozen = null;
        });
    }

    private void OnRegionSelected(PixelBounds region)
    {
        // Prefer cropping the frozen snapshot (WYSIWYG with the loupe); fall back to a fresh grab.
        if (_frozen is not null)
        {
            try
            {
                ShowInEditor(_frozen.Crop(region));
                return;
            }
            catch
            {
                // region outside the frozen buffer — fall through to a live capture
            }
        }

        CaptureAndEdit(region);
    }

    private void ShowInEditor(CapturedImage image)
    {
        var editor = _editor;
        if (editor is null)
        {
            editor = new EditorWindow();
            editor.Closed += (_, _) => { if (ReferenceEquals(_editor, editor)) _editor = null; };
            _editor = editor;
        }

        editor.SetCapture(image);

        // No desktop teleport: if the reused editor is parked on another desktop, bring it to the one
        // the user is looking at (the foreground window's desktop) rather than switching them there.
        if (editor.IsVisible)
        {
            var hwnd = editor.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (_desktops.IsWindowOnCurrentDesktop(hwnd) == false)
                _desktops.TryMoveToDesktopOf(hwnd, ForegroundWindow.Get());
        }

        editor.Show();
        editor.Activate();
    }
}
