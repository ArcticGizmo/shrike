using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Shrike.App.Native;

/// <summary>
/// Puts an image on the Windows clipboard in two formats at once: a registered <c>"PNG"</c> blob
/// (preserves fidelity for modern apps — Slack, browsers) and <c>CF_DIBV5</c> (so classic apps like
/// Office get a pasteable bitmap). Avalonia's clipboard can't express both native formats cleanly,
/// so this goes straight to the Win32 clipboard.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ClipboardImage
{
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_DIBV5 = 17;
    private const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>Set both PNG and CF_DIBV5 on the clipboard. Returns false if the clipboard was busy.</summary>
    public static bool Set(IntPtr ownerHwnd, byte[] pngBytes, byte[] dibV5Bytes)
    {
        if (!OpenClipboard(ownerHwnd))
            return false;
        try
        {
            EmptyClipboard();
            PlaceFormat(RegisterClipboardFormat("PNG"), pngBytes);
            PlaceFormat(CF_DIBV5, dibV5Bytes);
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Set clipboard text (CF_UNICODETEXT). Returns false if the clipboard was busy.</summary>
    public static bool SetText(IntPtr ownerHwnd, string text)
    {
        if (!OpenClipboard(ownerHwnd))
            return false;
        try
        {
            EmptyClipboard();
            PlaceFormat(CF_UNICODETEXT, System.Text.Encoding.Unicode.GetBytes(text + '\0'));
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void PlaceFormat(uint format, byte[] data)
    {
        if (format == 0 || data.Length == 0)
            return;

        var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)data.Length);
        if (hGlobal == IntPtr.Zero)
            return;

        var ptr = GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            return;
        }

        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        // Ownership of hGlobal transfers to the clipboard on success; free it only if the call failed.
        if (SetClipboardData(format, hGlobal) == IntPtr.Zero)
            GlobalFree(hGlobal);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
