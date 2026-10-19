using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Shrike.Capture;

/// <summary>
/// The handful of Direct3D 11 calls WGC capture needs — create a device, make a CPU-readable texture, copy
/// a sub-rectangle into it and read it back — called straight through the COM vtables rather than pulling
/// in a D3D wrapper library. Slot numbers are the method's position in the interface (inherited methods
/// first), as declared in <c>d3d11.h</c>. Not thread-safe: the immediate context is single-threaded, so
/// the owner serialises every call.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed unsafe class D3D11Device : IDisposable
{
    // IUnknown 0-2 · ID3D11Device: CreateBuffer 3, CreateTexture1D 4, CreateTexture2D 5.
    private const int DeviceCreateTexture2D = 5;
    // IUnknown 0-2 · ID3D11DeviceChild 3-6 · ID3D11DeviceContext: … Map 14, Unmap 15, … CopySubresourceRegion 46.
    private const int ContextMap = 14;
    private const int ContextUnmap = 15;
    private const int ContextCopySubresourceRegion = 46;
    // IUnknown 0-2 · ID3D11DeviceChild 3-6 · ID3D11Resource 7-9 · ID3D11Texture2D: GetDesc 10.
    private const int Texture2DGetDesc = 10;
    // IUnknown 0-2 · IDirect3DDxgiInterfaceAccess: GetInterface 3.
    private const int DxgiAccessGetInterface = 3;

    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20; // required for WGC interop
    private const uint D3D11_SDK_VERSION = 7;
    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const uint D3D11_USAGE_STAGING = 3;
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;
    private const uint D3D11_MAP_READ = 1;

    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");

    private IntPtr _device;
    private IntPtr _context;

    public D3D11Device()
    {
        var hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            IntPtr.Zero, 0, D3D11_SDK_VERSION, out _device, out _, out _context);
        Marshal.ThrowExceptionForHR(hr);
    }

    /// <summary>Wrap this device as the WinRT <see cref="IDirect3DDevice"/> the capture frame pool takes.</summary>
    public IDirect3DDevice CreateWinRTDevice()
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_device, in IID_IDXGIDevice, out var dxgi));
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
            try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
            finally { Marshal.Release(inspectable); }
        }
        finally
        {
            Marshal.Release(dxgi);
        }
    }

    /// <summary>A <paramref name="width"/>×<paramref name="height"/> BGRA texture the CPU can map and read.</summary>
    public IntPtr CreateStagingTexture(int width, int height)
    {
        var desc = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1,
            Usage = D3D11_USAGE_STAGING,
            CPUAccessFlags = D3D11_CPU_ACCESS_READ,
        };
        IntPtr texture;
        var create = (delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)Slot(_device, DeviceCreateTexture2D);
        Marshal.ThrowExceptionForHR(create(_device, &desc, IntPtr.Zero, &texture));
        return texture;
    }

    /// <summary>The ID3D11Texture2D behind a WGC frame surface (caller releases it).</summary>
    public static IntPtr TextureOf(IDirect3DSurface surface)
    {
        var abi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, in IID_IDirect3DDxgiInterfaceAccess, out var access));
            try
            {
                var iid = IID_ID3D11Texture2D;
                IntPtr texture;
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(access, DxgiAccessGetInterface);
                Marshal.ThrowExceptionForHR(getInterface(access, &iid, &texture));
                return texture;
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            MarshalInterface<IDirect3DSurface>.DisposeAbi(abi);
        }
    }

    /// <summary>GPU-copy the <paramref name="width"/>×<paramref name="height"/> rectangle at (x, y) of
    /// <paramref name="source"/> into the top-left of <paramref name="destination"/>. Returns false (copying
    /// nothing) when the rectangle doesn't fit inside the source.</summary>
    public bool CopyRegion(IntPtr source, IntPtr destination, int x, int y, int width, int height)
    {
        Texture2DDesc desc;
        ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, void>)Slot(source, Texture2DGetDesc))(source, &desc);
        if (x < 0 || y < 0 || x + width > desc.Width || y + height > desc.Height) return false;

        var box = new Box { Left = (uint)x, Top = (uint)y, Front = 0, Right = (uint)(x + width), Bottom = (uint)(y + height), Back = 1 };
        var copy = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)
            Slot(_context, ContextCopySubresourceRegion);
        copy(_context, destination, 0, 0, 0, 0, source, 0, &box);
        return true;
    }

    /// <summary>Map a staging texture and copy its rows, tightly packed, into <paramref name="bgra"/>.
    /// Waits for any pending GPU copy into it to finish.</summary>
    public void ReadTexture(IntPtr staging, byte[] bgra, int width, int height)
    {
        MappedSubresource mapped;
        var map = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, MappedSubresource*, int>)Slot(_context, ContextMap);
        Marshal.ThrowExceptionForHR(map(_context, staging, 0, D3D11_MAP_READ, 0, &mapped));
        try
        {
            var rowBytes = width * 4;
            fixed (byte* dst = bgra)
            {
                for (var y = 0; y < height; y++)
                    Buffer.MemoryCopy((byte*)mapped.Data + (long)y * mapped.RowPitch, dst + (long)y * rowBytes, rowBytes, rowBytes);
            }
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(_context, ContextUnmap))(_context, staging, 0);
        }
    }

    public void Dispose()
    {
        if (_context != IntPtr.Zero) { Marshal.Release(_context); _context = IntPtr.Zero; }
        if (_device != IntPtr.Zero) { Marshal.Release(_device); _device = IntPtr.Zero; }
    }

    private static void* Slot(IntPtr comObject, int index) => (*(void***)comObject)[index];

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;   // DXGI_SAMPLE_DESC.Count
        public uint SampleQuality; // DXGI_SAMPLE_DESC.Quality
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Box
    {
        public uint Left, Top, Front, Right, Bottom, Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public void* Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
