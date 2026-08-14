using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Shrike.Core.Capture;

namespace Shrike.App.Native;

/// <summary>
/// Snapshots the on-screen rectangles of the visible top-level windows, topmost first, for the
/// overlay's window snap-highlight. Enumerated once <b>before</b> the overlays are shown, so the
/// overlays themselves are never in the list.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TopLevelWindows
{
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;
    private const int MinWindowEdge = 8;

    /// <summary>Visible, un-cloaked top-level windows in Z order (topmost first).</summary>
    public static IReadOnlyList<PixelBounds> Enumerate()
    {
        var list = new List<PixelBounds>();
        var ownProcessId = (uint)Environment.ProcessId;

        EnumWindowsProc callback = (hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            // Never snap-select one of our own windows (chooser, dimmers, overlays, editor) — they may
            // still be open (teardown is deferred) when the region overlay enumerates windows.
            GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == ownProcessId)
                return true;

            // Skip windows cloaked by the shell (other virtual desktops, suspended UWP, etc.).
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            // Skip the desktop shell itself. Progman/WorkerW span the whole virtual desktop, so snapping
            // to them would turn a bare-desktop click into an all-monitors grab. Dropping them lets such a
            // click fall through to per-monitor capture (see OverlayWindow's monitor fallback).
            var cls = ClassNameOf(hwnd);
            if (cls is "Progman" or "WorkerW")
                return true;

            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0
                && !GetWindowRect(hwnd, out r))
            {
                return true;
            }

            // Skip minimized (parked at -32000) and degenerate windows.
            if (r.left <= -30000 || r.top <= -30000)
                return true;

            var bounds = new PixelBounds(r.left, r.top, r.right - r.left, r.bottom - r.top);
            if (bounds.Width >= MinWindowEdge && bounds.Height >= MinWindowEdge)
                list.Add(bounds);

            return true;
        };

        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return list;
    }

    /// <summary>The topmost window whose bounds contain the physical point, or null.</summary>
    public static PixelBounds? TopmostAt(IReadOnlyList<PixelBounds> windows, int x, int y)
    {
        foreach (var w in windows)
        {
            if (x >= w.X && x < w.Right && y >= w.Y && y < w.Bottom)
                return w;
        }
        return null;
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        var len = GetClassName(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
