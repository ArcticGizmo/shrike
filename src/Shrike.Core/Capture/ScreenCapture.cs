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

    /// <summary>Capture a rectangle (physical pixels). Throws if the region is empty or GDI fails.</summary>
    public static CapturedImage Capture(PixelBounds region)
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
}
