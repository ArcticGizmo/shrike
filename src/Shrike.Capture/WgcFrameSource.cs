using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Shrike.Core.Capture;
using Shrike.Core.Recording;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Shrike.Capture;

/// <summary>
/// Records a screen region with Windows.Graphics.Capture — the GPU replacement for <c>GdiFrameSource</c>,
/// whose <c>BitBlt</c> waits on DWM for ~33 ms a grab (a whole 30 fps frame budget). WGC hands us each
/// composed frame of the region's monitor as a D3D11 texture as soon as it changes; we copy just the region
/// into a CPU-readable staging texture on the GPU and only read it back when the recorder asks.
/// <para>
/// Frames arrive only when the screen changes, so <see cref="CaptureFrame"/> returns the latest one — a
/// static screen costs nothing. The cursor is composited by WGC itself, and the yellow capture border is
/// turned off where Windows allows it (Windows 11). Windows hidden with <c>WDA_EXCLUDEFROMCAPTURE</c> (the
/// HUD) stay hidden, exactly as with GDI. <see cref="TryCreate"/> returns null when WGC can't record this
/// region — unsupported OS, a region straddling monitors, no GPU device — so the caller falls back to GDI.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed unsafe class WgcFrameSource : IFrameSource
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private readonly D3D11Device _d3d;
    private readonly IDirect3DDevice _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly IntPtr _staging;      // region-sized, CPU-readable copy of the latest frame
    private readonly int _originX, _originY; // region's top-left within the monitor
    private readonly byte[] _frame;
    private readonly ManualResetEventSlim _firstFrame = new();
    private SizeInt32 _poolSize;
    private bool _fresh;                   // _staging holds a frame _frame hasn't read yet
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }

    /// <summary>Start capturing <paramref name="region"/> (physical virtual-screen pixels), or return null if
    /// WGC can't — the caller should fall back to GDI. Blocks until the first frame arrives (normally a few
    /// ms), so a returned source always has a real frame to give.</summary>
    public static WgcFrameSource? TryCreate(PixelBounds region, bool captureCursor = true)
    {
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) return null;
            var r = EvenSized(region);
            if (MonitorLookup.Containing(r) is not { } monitor) return null; // spans monitors / off-screen
            return new WgcFrameSource(r, monitor.Handle, monitor.Bounds, captureCursor);
        }
        catch
        {
            return null; // no hardware device, capture blocked by policy, … — GDI still works
        }
    }

    private WgcFrameSource(PixelBounds region, IntPtr hmon, PixelBounds monitor, bool captureCursor)
    {
        Width = region.Width;
        Height = region.Height;
        _originX = region.X - monitor.X;
        _originY = region.Y - monitor.Y;
        _frame = new byte[Width * Height * 4];

        _d3d = new D3D11Device();
        try
        {
            _device = _d3d.CreateWinRTDevice();
            _staging = _d3d.CreateStagingTexture(Width, Height);
            _item = CaptureItemInterop.ForMonitor(hmon);
            _poolSize = _item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, PixelFormat, 2, _poolSize);
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = captureCursor;
            TryDisableBorder(_session);

            _pool.FrameArrived += OnFrameArrived;
            _session.StartCapture();
            if (!_firstFrame.Wait(FirstFrameTimeout))
                throw new InvalidOperationException("Windows.Graphics.Capture delivered no frame.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The latest frame of the region as top-down BGRA. The array is reused: it's valid until the
    /// next call, which is all the recorder needs (it writes each frame out before grabbing the next).</summary>
    public byte[] CaptureFrame()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_fresh)
            {
                _d3d.ReadTexture(_staging, _frame, Width, Height);
                _fresh = false;
            }
            return _frame;
        }
    }

    // Raised on a thread-pool thread (free-threaded pool). Only does a GPU-side copy of the region; the
    // read-back waits until the recorder actually wants a frame. Never throws: an exception here would take
    // the app down, and a missed frame just means the recorder repeats the previous one.
    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object args)
    {
        try
        {
            using var frame = pool.TryGetNextFrame();
            if (frame is null) return;
            lock (_lock)
            {
                if (_disposed) return;
                var texture = D3D11Device.TextureOf(frame.Surface);
                try
                {
                    // A resolution change can leave the region outside the new frame; keep the last good
                    // frame rather than copy out of bounds.
                    if (_d3d.CopyRegion(texture, _staging, _originX, _originY, Width, Height))
                        _fresh = true;
                }
                finally
                {
                    Marshal.Release(texture);
                }
                _firstFrame.Set();

                // The monitor's mode changed: resize the pool so later frames match it.
                var size = frame.ContentSize;
                if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
                {
                    _poolSize = size;
                    _pool.Recreate(_device, PixelFormat, 2, _poolSize);
                }
            }
        }
        catch
        {
            // Device lost, monitor unplugged, … — keep serving the last frame.
        }
    }

    private static void TryDisableBorder(GraphicsCaptureSession session)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        try
        {
            // Unpackaged apps are granted borderless capture; the request just records that we asked.
            GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().Wait(1000);
            session.IsBorderRequired = false;
        }
        catch
        {
            // Not allowed here — the yellow border is cosmetic and sits outside the recorded region anyway.
        }
    }

    // yuv420p needs even width/height; trim a pixel rather than pad (same rule as GdiFrameSource).
    private static PixelBounds EvenSized(PixelBounds region)
    {
        var r = region.Normalized();
        var w = r.Width - (r.Width & 1);
        var h = r.Height - (r.Height & 1);
        if (w <= 0 || h <= 0)
            throw new ArgumentException("Recording region is too small.", nameof(region));
        return new PixelBounds(r.X, r.Y, w, h);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Constructed-in-order fields may be null if the constructor threw part-way.
        if (_pool is not null) _pool.FrameArrived -= OnFrameArrived;
        _session?.Dispose();
        _pool?.Dispose();
        lock (_lock)
        {
            if (_staging != IntPtr.Zero) Marshal.Release(_staging);
            _d3d?.Dispose();
            _firstFrame.Dispose(); // under the lock: the frame handler only Sets it after checking _disposed
        }
    }
}
