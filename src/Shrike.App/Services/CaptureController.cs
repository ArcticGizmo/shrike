using System.Runtime.Versioning;
using Avalonia;
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
    private readonly RecentRing _ring;
    private readonly Action? _onOverlayShown;

    private readonly List<OverlayWindow> _overlays = [];
    private readonly List<DimWindow> _dimmers = [];
    private RegionSelectionSession? _session;
    private CapturedImage? _frozen;
    private EditorWindow? _editor;
    private CaptureMenuWindow? _menu;

    public CaptureController(VirtualDesktopService desktops, RecentRing ring, Action? onOverlayShown = null)
    {
        _desktops = desktops;
        _ring = ring;
        _onOverlayShown = onOverlayShown;
    }

    /// <summary>
    /// Open the capture chooser at the cursor. The single hotkey and the tray both route here; the
    /// chosen mode then runs. This is the one entry point (and the seam where "Record" will slot in).
    /// </summary>
    public void ShowCaptureMenu()
    {
        if (_menu is not null)
        {
            _menu.Activate();
            return;
        }

        var monitors = MonitorsOrFallback();

        // Freeze the CLEAN desktop first — before we dim — so no scrim ever ends up in a capture.
        // Every mode works from this snapshot.
        var frozen = TryCaptureFrozen();

        // Dim every monitor behind the chooser to signal "you're mid-capture". A click on any dimmer
        // (i.e. outside the chooser) cancels.
        foreach (var monitor in monitors)
        {
            var dim = new DimWindow(monitor);
            dim.Dismissed += TeardownChooser;
            _dimmers.Add(dim);
            dim.Show();
        }

        var (cx, cy) = CursorPosition.Get();
        var menu = new CaptureMenuWindow(new PixelPoint(cx, cy), _ring.Count);
        menu.Chosen += choice =>
        {
            TeardownChooser();
            RunChoice(choice, frozen);
        };
        menu.Cancelled += TeardownChooser;
        menu.Closed += (_, _) => { if (ReferenceEquals(_menu, menu)) _menu = null; };

        _menu = menu;
        menu.Show();     // shown last, so it sits above the dimmers and takes focus
        menu.Activate();
    }

    private void RunChoice(CaptureMenuChoice choice, CapturedImage? frozen)
    {
        switch (choice)
        {
            case CaptureMenuChoice.Region:
                BeginRegionCapture(frozen);
                break;
            case CaptureMenuChoice.Monitor:
                CaptureFromFrozen(frozen, MonitorUnderCursorBounds());
                break;
            case CaptureMenuChoice.AllMonitors:
                CaptureFromFrozen(frozen, ScreenCapture.VirtualScreenBounds());
                break;
            case CaptureMenuChoice.Recent:
                // Open the editor on the newest capture; the filmstrip surfaces the rest.
                if (_ring.Items.Count > 0)
                    OpenInEditor(_ring.Items[0].Image);
                break;
        }
    }

    private void TeardownChooser()
    {
        // Defer so we never close a window from inside its own pointer/key event.
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var dim in _dimmers.ToArray())
                dim.Close();
            _dimmers.Clear();

            _menu?.Close();
            _menu = null;
        });
    }

    /// <summary>Show a region-selection overlay on each monitor (or focus the existing set).</summary>
    public void BeginRegionCapture(CapturedImage? frozen = null)
    {
        if (_overlays.Count > 0)
        {
            _overlays[0].Activate();
            return;
        }

        var monitors = MonitorsOrFallback();

        // Reuse the chooser's clean snapshot when there is one; otherwise freeze now. The magnifier
        // samples this and the final selection is cropped from it (WYSIWYG with the loupe).
        _frozen = frozen ?? TryCaptureFrozen();

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

    private static IReadOnlyList<MonitorInfo> MonitorsOrFallback()
    {
        var monitors = Monitors.All();
        return monitors.Count > 0
            ? monitors
            : [new MonitorInfo(ScreenCapture.VirtualScreenBounds(), 1.0, true)];
    }

    private static PixelBounds MonitorUnderCursorBounds()
    {
        var (cx, cy) = CursorPosition.Get();
        var monitor = Monitors.All().FirstOrDefault(m =>
            cx >= m.Bounds.X && cx < m.Bounds.Right && cy >= m.Bounds.Y && cy < m.Bounds.Bottom);
        return monitor.Bounds.IsEmpty ? ScreenCapture.VirtualScreenBounds() : monitor.Bounds;
    }

    /// <summary>Crop the given clean snapshot to <paramref name="bounds"/>, or grab live if there's none.</summary>
    private void CaptureFromFrozen(CapturedImage? frozen, PixelBounds bounds)
    {
        if (bounds.IsEmpty)
            return;

        if (frozen is not null)
        {
            try
            {
                ShowInEditor(frozen.Crop(bounds));
                return;
            }
            catch
            {
                // bounds fell outside the snapshot — fall back to a live grab
            }
        }

        CaptureAndEdit(bounds);
    }

    /// <summary>Dismiss the overlays if open (used by the measure-startup path).</summary>
    public void CancelOverlay() => _session?.Cancel();

    /// <summary>Capture the whole (all-monitor) desktop straight to the editor.</summary>
    public void CaptureFullScreen() => CaptureAndEdit(ScreenCapture.VirtualScreenBounds());

    /// <summary>Capture the monitor the pointer is currently on (live — for the CLI/IPC path).</summary>
    public void CaptureMonitorUnderCursor() => CaptureAndEdit(MonitorUnderCursorBounds());

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

    /// <summary>Re-open a capture already in the recent ring, without pushing a duplicate entry.</summary>
    public void OpenInEditor(CapturedImage image) => ShowInEditor(image, addToRing: false);

    private void ShowInEditor(CapturedImage image, bool addToRing = true)
    {
        if (addToRing)
            _ring.Add(image);

        var editor = _editor;
        if (editor is null)
        {
            editor = new EditorWindow();
            editor.Closed += (_, _) => { if (ReferenceEquals(_editor, editor)) _editor = null; };
            _editor = editor;
        }

        // Wire the strip to the ring + re-open path (idempotent; the editor guards re-subscribe).
        editor.AttachRecentRing(_ring, OpenInEditor);
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
