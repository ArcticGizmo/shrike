namespace Shrike.Core.Recording;

/// <summary>
/// A source of video frames at a fixed size — the seam between "how we grab pixels" and "how we
/// encode them": GPU capture (<c>WgcFrameSource</c>) where it can serve the region, GDI otherwise,
/// without the recorder or encoder changing. Frames are top-down BGRA, Width*Height*4 bytes.
/// </summary>
public interface IFrameSource : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>Grab the current frame as top-down BGRA (Width*Height*4 bytes). The array may be reused by
    /// the source, so it is only valid until the next call.</summary>
    byte[] CaptureFrame();
}
