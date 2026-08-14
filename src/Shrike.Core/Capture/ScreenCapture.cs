using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Shrike.Core.Capture;

/// <summary>
/// Grabs still pixels off the screen with a plain GDI <c>BitBlt</c> — borderless, instant and
/// DPI-correct (the app manifest declares per-monitor-v2 awareness, so coordinates are physical
/// pixels). This is the M1 primary for region/monitor/full captures. Occlusion-correct window
/// capture and continuous recording move to Windows.Graphics.Capture at M4.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScreenCapture
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000; // include layered windows (cursors, tooltips)

    /// <summary>The bounding rectangle of all monitors, in physical virtual-screen coordinates.</summary>
    public static PixelBounds VirtualScreenBounds() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>
    /// Capture a rectangle (physical pixels). Throws if the region is empty or GDI fails.
    /// The <c>BitBlt</c> uses <c>CAPTUREBLT</c> so layered windows (tooltips, Shrike's own spotlight
    /// glow) are included — but on modern Windows the mouse cursor is a hardware overlay that
    /// <c>BitBlt</c> never sees, with or without that flag. So when <paramref name="drawCursor"/> is set
    /// we composite the live cursor into the frame ourselves (<see cref="DrawCursorInto"/>); recordings
    /// pass the user's preference, stills leave it off.
    /// </summary>
    public static CapturedImage Capture(PixelBounds region, bool drawCursor = false)
    {
        var r = region.Normalized();
        if (r.IsEmpty)
            throw new ArgumentException("Capture region is empty.", nameof(region));

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            throw new InvalidOperationException("GetDC(screen) failed.");

        var memDc = IntPtr.Zero;
        var hBmp = IntPtr.Zero;
        var oldBmp = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            hBmp = CreateCompatibleBitmap(screenDc, r.Width, r.Height);
            if (memDc == IntPtr.Zero || hBmp == IntPtr.Zero)
                throw new InvalidOperationException("GDI object allocation failed.");

            oldBmp = SelectObject(memDc, hBmp);

            if (!BitBlt(memDc, 0, 0, r.Width, r.Height, screenDc, r.X, r.Y, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException($"BitBlt failed (0x{Marshal.GetLastWin32Error():X8}).");

            // The cursor isn't in the BitBlt (hardware overlay), so paint it on when asked, before we
            // read the pixels out. DrawIconEx clips to the DC, so an off-region cursor is a no-op.
            if (drawCursor)
                DrawCursorInto(memDc, r);

            var bgra = new byte[r.Width * r.Height * 4];
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = r.Width,
                biHeight = -r.Height, // negative => top-down rows
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            };

            if (GetDIBits(memDc, hBmp, 0, (uint)r.Height, bgra, ref header, DIB_RGB_COLORS) == 0)
                throw new InvalidOperationException("GetDIBits failed.");

            // BitBlt leaves the alpha byte undefined; screenshots are opaque, so force it.
            for (var i = 3; i < bgra.Length; i += 4)
                bgra[i] = 255;

            return new CapturedImage(r.Width, r.Height, bgra, r, DateTimeOffset.Now);
        }
        finally
        {
            if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
            if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Paint the live mouse cursor into <paramref name="memDc"/> at its screen position, mapped into the
    /// region's local pixels and offset by the cursor's hotspot so the tip lands where the pointer is.
    /// Skipped when the cursor is hidden/suppressed. The <c>GetIconInfo</c> bitmaps are freed each call.
    /// </summary>
    private static void DrawCursorInto(IntPtr memDc, PixelBounds region)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING || ci.hCursor == IntPtr.Zero)
            return;

        int xHot = 0, yHot = 0;
        if (GetIconInfo(ci.hCursor, out var info))
        {
            xHot = (int)info.xHotspot;
            yHot = (int)info.yHotspot;
            // GetIconInfo hands back two owned bitmaps; free them so we don't leak GDI objects per frame.
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
        }

        DrawIconEx(memDc, ci.ptScreenPos.X - region.X - xHot, ci.ptScreenPos.Y - region.Y - yHot,
            ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
    }

    private const int CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy,
        IntPtr hdcSrc, int x1, int y1, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines,
        [Out] byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);
}
