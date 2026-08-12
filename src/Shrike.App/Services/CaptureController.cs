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
            var overlay = new OverlayWindow(session, monitor);
            _overlays.Add(overlay);
            overlay.Show();
        }

        _onOverlayShown?.Invoke();
    }

    /// <summary>Dismiss the overlays if open (used by the measure-startup path).</summary>
    public void CancelOverlay() => _session?.Cancel();

    private void CloseOverlays()
    {
        // Defer so we never close a window from inside its own pointer/key event.
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var overlay in _overlays.ToArray())
                overlay.Close();
            _overlays.Clear();
            _session = null;
        });
    }

    private void OnRegionSelected(PixelBounds region)
    {
        CapturedImage image;
        try
        {
            image = ScreenCapture.Capture(region);
        }
        catch
        {
            return; // a failed grab (e.g. protected content) just aborts this capture
        }

        ShowInEditor(image);
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
