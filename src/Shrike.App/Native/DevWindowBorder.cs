using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Shrike.App.Native;

/// <summary>
/// Paints a window's DWM frame border pink so a dev instance is obvious at a glance next to the installed
/// Shrike. Uses <c>DWMWA_BORDER_COLOR</c>, which is honoured on Windows 11 (22000+) and silently ignored on
/// older builds — purely cosmetic, so failure is fine.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DevWindowBorder
{
    private const int DWMWA_BORDER_COLOR = 34;

    // COLORREF is 0x00BBGGRR — this is #FF4FA3 (hot pink).
    private const int Pink = 0x00A34FFF;

    public static void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var color = Pink;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref color, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
