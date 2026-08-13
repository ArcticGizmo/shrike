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

    /// <summary>Exclude <paramref name="hwnd"/> from screen capture. Returns true if the OS applied it.</summary>
    public static bool Hide(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        // Prefer full exclusion (content behind shows through); fall back to the black-box mode on
        // pre-2004 builds so the HUD at least never leaks its own controls into the recording.
        return SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)
            || SetWindowDisplayAffinity(hwnd, WDA_MONITOR);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
