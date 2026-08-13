namespace Shrike.Core.Recording;

/// <summary>
/// Sink for a stream of top-down BGRA frames that produces an encoded video file. Implementations own
/// the codec/container; the caller (a recording session) owns pacing and simply pushes one frame per
/// output frame. Not required to be thread-safe — drive from one thread.
/// </summary>
public interface IFrameEncoder : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>Append one frame. <paramref name="bgra"/> is top-down BGRA of exactly Width*Height*4 bytes.</summary>
    void WriteFrame(byte[] bgra);

    /// <summary>Flush and finalise the output file. Call once when recording stops.</summary>
    void Finish();
}
