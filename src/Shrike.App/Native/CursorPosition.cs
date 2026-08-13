using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Shrike.App.Native;

/// <summary>The pointer's current position in physical screen (virtual-desktop) pixels.</summary>
[SupportedOSPlatform("windows")]
internal static class CursorPosition
{
    public static (int X, int Y) Get() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
