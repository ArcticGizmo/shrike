using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Shrike.Core.Recording;

namespace Shrike.App.Native;

/// <summary>
/// A global low-level mouse hook (<c>WH_MOUSE_LL</c>) that reports pointer moves and button transitions
/// — the live source behind the smooth-cursor track. It must be installed on a thread with a message
/// pump (the Avalonia UI thread) and torn down when recording ends. The callback fires on every mouse
/// move, so it does the absolute minimum (parse + raise) and returns immediately; a slow callback risks
/// Windows silently dropping the hook. Positions are in virtual-screen physical pixels (the process is
/// per-monitor-v2 DPI aware).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int HC_ACTION = 0;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;

    private readonly LowLevelMouseProc _proc; // held so the delegate isn't collected while hooked
    private IntPtr _hook;

    /// <summary>Pointer moved to (x, y) in virtual-screen physical pixels.</summary>
    public event Action<int, int>? Moved;

    /// <summary>A button went down (true) or up (false).</summary>
    public event Action<MouseButtonKind, bool>? Clicked;

    public MouseHook() => _proc = HookCallback;

    /// <summary>Install the hook. Call on a thread that pumps messages (the UI thread). Idempotent.</summary>
    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION)
        {
            var msg = (int)wParam;
            if (msg == WM_MOUSEMOVE)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                Moved?.Invoke(data.pt.x, data.pt.y);
            }
            else
            {
                switch (msg)
                {
                    case WM_LBUTTONDOWN: Clicked?.Invoke(MouseButtonKind.Left, true); break;
                    case WM_LBUTTONUP: Clicked?.Invoke(MouseButtonKind.Left, false); break;
                    case WM_RBUTTONDOWN: Clicked?.Invoke(MouseButtonKind.Right, true); break;
                    case WM_RBUTTONUP: Clicked?.Invoke(MouseButtonKind.Right, false); break;
                    case WM_MBUTTONDOWN: Clicked?.Invoke(MouseButtonKind.Middle, true); break;
                    case WM_MBUTTONUP: Clicked?.Invoke(MouseButtonKind.Middle, false); break;
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
