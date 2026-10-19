using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.Capture;

namespace Shrike.Capture;

/// <summary>
/// Makes a <see cref="GraphicsCaptureItem"/> for a monitor without the picker UI, via the classic-COM
/// <c>IGraphicsCaptureItemInterop</c> on the item's activation factory (the documented route for desktop
/// apps).
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static unsafe class CaptureItemInterop
{
    // IUnknown 0-2 · IGraphicsCaptureItemInterop: CreateForWindow 3, CreateForMonitor 4.
    private const int CreateForMonitor = 4;

    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");

    public static GraphicsCaptureItem ForMonitor(IntPtr hmonitor)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, (uint)className.Length, out var hstring));
        try
        {
            var factoryIid = IID_IGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, &factoryIid, out var interop));
            try
            {
                var itemIid = IID_IGraphicsCaptureItem;
                IntPtr item;
                var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(void***)interop)[CreateForMonitor];
                Marshal.ThrowExceptionForHR(create(interop, hmonitor, &itemIid, &item));
                try { return GraphicsCaptureItem.FromAbi(item); }
                finally { Marshal.Release(item); }
            }
            finally
            {
                Marshal.Release(interop);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, Guid* iid, out IntPtr factory);
}
