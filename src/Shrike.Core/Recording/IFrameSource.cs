namespace Shrike.Core.Recording;

/// <summary>
/// A source of video frames at a fixed size — the seam between "how we grab pixels" and "how we
/// encode them". The GDI implementation ships first; a Windows.Graphics.Capture source can drop in
/// later without the recorder or encoder changing. Frames are top-down BGRA, Width*Height*4 bytes.
/// </summary>
public interface IFrameSource : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>Grab the current frame as top-down BGRA (Width*Height*4 bytes).</summary>
    byte[] CaptureFrame();
}
