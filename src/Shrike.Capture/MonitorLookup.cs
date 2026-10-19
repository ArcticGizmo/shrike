using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Shrike.Core.Capture;

namespace Shrike.Capture;

/// <summary>Finds the single monitor that wholly contains a region (WGC captures one monitor at a time).</summary>
[SupportedOSPlatform("windows")]
internal static class MonitorLookup
{
    private const uint MONITOR_DEFAULTTONULL = 0;

    /// <summary>The monitor containing all of <paramref name="region"/> (physical pixels), with its bounds;
    /// null if the region straddles monitors or lies off-screen.</summary>
    public static (IntPtr Handle, PixelBounds Bounds)? Containing(PixelBounds region)
    {
        var rect = new RECT { Left = region.X, Top = region.Y, Right = region.X + region.Width, Bottom = region.Y + region.Height };
        var hmon = MonitorFromRect(ref rect, MONITOR_DEFAULTTONULL);
        if (hmon == IntPtr.Zero) return null;

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hmon, ref info)) return null;
        var m = info.rcMonitor;
        if (rect.Left < m.Left || rect.Top < m.Top || rect.Right > m.Right || rect.Bottom > m.Bottom) return null;
        return (hmon, new PixelBounds(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
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
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
