using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Shrike.App.Native;

/// <summary>
/// A hidden message-only (<c>HWND_MESSAGE</c>) window that owns the app's global hotkey registrations
/// and receives <c>WM_HOTKEY</c>. It must be created on the Avalonia UI thread — that thread runs the
/// Win32 message pump, so this window's <c>WndProc</c> is dispatched there and <see cref="HotkeyPressed"/>
/// fires on the UI thread (safe to show windows directly).
/// </summary>
/// <remarks>
/// M0 keeps this small, stable Win32 surface as hand-rolled P/Invoke. CsWin32 is adopted at M1, where
/// the Windows.Graphics.Capture surface is large enough to earn a source generator.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class MessageWindow : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    // Held so the unmanaged function pointer stays alive for the window's lifetime.
    private readonly WndProcDelegate _wndProc;
    private readonly string _className;
    private readonly IntPtr _hInstance;
    private bool _disposed;

    /// <summary>Raised on the UI thread with the hotkey id when a registered combo fires.</summary>
    public event Action<int>? HotkeyPressed;

    public IntPtr Handle { get; }

    public MessageWindow(string className)
    {
        _className = className;
        _hInstance = GetModuleHandleW(null);
        _wndProc = WindowProc;

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            lpszClassName = _className,
        };

        if (RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException(
                $"RegisterClassEx failed (0x{Marshal.GetLastWin32Error():X8}).");

        Handle = CreateWindowExW(0, _className, _className, 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"CreateWindowEx failed (0x{Marshal.GetLastWin32Error():X8}).");
    }

    /// <summary>Register a global hotkey. Returns false if the OS refused (e.g. already owned).</summary>
    public bool RegisterHotkey(int id, uint modifiers, uint virtualKey)
        => RegisterHotKey(Handle, id, modifiers, virtualKey);

    public void UnregisterHotkey(int id) => UnregisterHotKey(Handle, id);

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
            HotkeyPressed?.Invoke((int)wParam);
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Handle != IntPtr.Zero) DestroyWindow(Handle);
        UnregisterClassW(_className, _hInstance);
        GC.KeepAlive(_wndProc);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW unnamedParam1);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
