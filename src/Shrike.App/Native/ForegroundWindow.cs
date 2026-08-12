using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Shrike.App.Native;

/// <summary>The current foreground window — a reliable "a window on the desktop the user is looking at"
/// reference for the virtual-desktop move (the public COM API can't name the current desktop directly).</summary>
[SupportedOSPlatform("windows")]
internal static class ForegroundWindow
{
    public static IntPtr Get() => GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
