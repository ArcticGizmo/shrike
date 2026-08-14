using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Shrike.App.Native;

/// <summary>
/// Hides a window from screen capture — screenshots and Shrike's own GDI recorder alike — via
/// <c>SetWindowDisplayAffinity</c>. The window still shows on the physical display; DWM just keeps it
/// out of the capture path. This is how the recording HUD stays out of its own recording even when the
/// captured region is the whole screen.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowExclusion
{
    private const uint WDA_MONITOR = 0x00000001;          // renders black in captures (Win7+)
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011; // excluded entirely (Win10 2004+)

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Exclude <paramref name="hwnd"/> from screen capture. Returns true if the OS applied it.</summary>
    public static bool Hide(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        // Prefer full exclusion (content behind shows through); fall back to the black-box mode on
        // pre-2004 builds so the HUD at least never leaks its own controls into the recording.
        return SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)
            || SetWindowDisplayAffinity(hwnd, WDA_MONITOR);
    }

    /// <summary>Make <paramref name="hwnd"/> transparent to the mouse — clicks fall through to whatever's
    /// underneath (used by the recording frame so it never gets in the user's way).</summary>
    public static void MakeClickThrough(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT | WS_EX_LAYERED));
    }

    /// <summary>Stop <paramref name="hwnd"/> stealing activation or z-order when clicked: it still receives
    /// the mouse (so its handles stay draggable) but never rises above — or takes focus from — other
    /// windows. Used so dragging the region frame never buries the HUD floating over its scrim.</summary>
    public static void MakeNonActivating(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
