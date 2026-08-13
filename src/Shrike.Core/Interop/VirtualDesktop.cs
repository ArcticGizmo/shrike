using System.Runtime.InteropServices;

namespace Shrike.Core.Interop;

/// <summary>
/// Documented public COM interface for querying/moving windows across virtual desktops. We restrict
/// ourselves to this stable surface (not the per-build internal API) so Shrike doesn't break on a
/// Windows update — see design §5. IID {a5cd92ff-29be-454c-8d04-d82879fb3f1b}.
/// </summary>
[ComImport]
[Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    [PreserveSig]
    int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);

    [PreserveSig]
    int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

    [PreserveSig]
    int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

/// <summary>
/// Thin, defensive wrapper over <see cref="IVirtualDesktopManager"/>. Every call degrades to a null
/// / false result rather than throwing, so a COM hiccup on some Windows build can never crash a
/// capture — it just falls back to default window behaviour.
/// </summary>
public sealed class VirtualDesktopService
{
    // CLSID_VirtualDesktopManager.
    private static readonly Guid ClsidVirtualDesktopManager = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");

    private readonly IVirtualDesktopManager? _manager;

    private VirtualDesktopService(IVirtualDesktopManager? manager) => _manager = manager;

    /// <summary>True when the COM manager was created and calls can be attempted.</summary>
    public bool Available => _manager is not null;

    /// <summary>Create the service, swallowing any activation failure into an unavailable instance.</summary>
    public static VirtualDesktopService Create()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidVirtualDesktopManager, throwOnError: false);
            var instance = type is not null ? Activator.CreateInstance(type) : null;
            return new VirtualDesktopService(instance as IVirtualDesktopManager);
        }
        catch
        {
            return new VirtualDesktopService(null);
        }
    }

    /// <summary>
    /// Is the given top-level window on the desktop the user is currently looking at? Null when the
    /// answer is unknown (manager unavailable or the call failed).
    /// </summary>
    public bool? IsWindowOnCurrentDesktop(IntPtr hwnd)
    {
        if (_manager is null || hwnd == IntPtr.Zero) return null;
        try
        {
            return _manager.IsWindowOnCurrentVirtualDesktop(hwnd, out var onCurrent) == 0
                ? onCurrent != 0
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The desktop GUID a window lives on, or null if it can't be determined.</summary>
    public Guid? GetWindowDesktopId(IntPtr hwnd)
    {
        if (_manager is null || hwnd == IntPtr.Zero) return null;
        try
        {
            return _manager.GetWindowDesktopId(hwnd, out var id) == 0 ? id : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Move <paramref name="hwnd"/> onto the desktop that <paramref name="referenceOnCurrentDesktop"/>
    /// occupies. The public API can't name "the current desktop" directly, so callers pass a window
    /// known to be on it (e.g. the foreground window). Returns false if the move couldn't be made.
    /// </summary>
    public bool TryMoveToDesktopOf(IntPtr hwnd, IntPtr referenceOnCurrentDesktop)
    {
        if (_manager is null || hwnd == IntPtr.Zero) return false;
        if (GetWindowDesktopId(referenceOnCurrentDesktop) is not { } target) return false;
        try
        {
            return _manager.MoveWindowToDesktop(hwnd, ref target) == 0;
        }
        catch
        {
            return false;
        }
    }
}
