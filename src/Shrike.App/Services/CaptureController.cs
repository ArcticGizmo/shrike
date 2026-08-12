using System.Runtime.Versioning;
using Shrike.App.Native;
using Shrike.App.Views;
using Shrike.Core.Capture;
using Shrike.Core.Interop;

namespace Shrike.App.Services;

/// <summary>
/// Orchestrates a capture: pop the region overlay → grab the pixels → show them in the (reused)
/// editor. Enforces the no-desktop-switch rule when surfacing the editor.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class CaptureController
{
    private readonly VirtualDesktopService _desktops;
    private readonly Action? _onOverlayShown;

    private OverlayWindow? _overlay;
    private EditorWindow? _editor;

    public CaptureController(VirtualDesktopService desktops, Action? onOverlayShown = null)
    {
        _desktops = desktops;
        _onOverlayShown = onOverlayShown;
    }

    /// <summary>Show the region-selection overlay (or focus it if already up).</summary>
    public void BeginRegionCapture()
    {
        if (_overlay is not null)
        {
            _overlay.Activate();
            return;
        }

        var overlay = new OverlayWindow(_desktops);
        overlay.RegionSelected += OnRegionSelected;
        overlay.Cancelled += () => { };
        overlay.Closed += (_, _) => { if (ReferenceEquals(_overlay, overlay)) _overlay = null; };

        _overlay = overlay;
        overlay.Show();
        _onOverlayShown?.Invoke();
    }

    /// <summary>Dismiss the overlay if it's open (used by the measure-startup path).</summary>
    public void CancelOverlay() => _overlay?.Close();

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
