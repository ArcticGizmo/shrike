using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Shrike.Core.Capture;

namespace Shrike.App.Native;

/// <summary>
/// Resolves a window's on-screen rectangle. Prefers the DWM extended frame bounds — the true visible
/// rectangle — over <c>GetWindowRect</c>, which on Win10/11 includes the invisible resize border and
/// would capture a few pixels of empty margin.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowBounds
{
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>The physical-pixel bounds of the current foreground window, if there is one.</summary>
    public static bool TryForegroundWindow(out PixelBounds bounds)
    {
        bounds = default;

        var hwnd = ForegroundWindow.Get();
        if (hwnd == IntPtr.Zero)
            return false;

        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0
            && !GetWindowRect(hwnd, out r))
        {
            return false;
        }

        bounds = new PixelBounds(r.left, r.top, r.right - r.left, r.bottom - r.top);
        return !bounds.IsEmpty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
