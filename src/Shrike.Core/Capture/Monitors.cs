using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Shrike.Core.Capture;

/// <summary>One monitor: its physical-pixel bounds and DPI scale factor (1.0 = 96 dpi).</summary>
public readonly record struct MonitorInfo(PixelBounds Bounds, double Scale, bool IsPrimary);

/// <summary>
/// Enumerates the physical geometry and per-monitor DPI of every display, straight from Win32 — so
/// the overlay can place a correctly-sized, correctly-scaled window on each monitor without relying
/// on the UI framework's screen model.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Monitors
{
    private const int MONITORINFOF_PRIMARY = 1;
    private const int MDT_EFFECTIVE_DPI = 0;

    public static IReadOnlyList<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();

        // Delegate kept in a local so it stays alive across the (synchronous) enumeration call.
        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                var r = info.rcMonitor;
                var bounds = new PixelBounds(r.left, r.top, r.right - r.left, r.bottom - r.top);

                var scale = 1.0;
                if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                    scale = dpiX / 96.0;

                list.Add(new MonitorInfo(bounds, scale, (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        };

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return list;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
