namespace Shrike.Core.Recording;

/// <summary>
/// A finished M4 recording: the on-disk high-quality H.264 source plus the facts M5 needs to edit and
/// export it without re-probing — pixel size, output frame rate, and total duration. The recorder hands
/// one of these straight to the timeline editor at stop; a source re-opened from disk later can be
/// rebuilt by probing the file. Immutable — the timeline layers a segment list on top, and export
/// re-encodes from <see cref="Path"/>; the source file itself is never mutated.
/// </summary>
public sealed record RecordingSource(string Path, int Width, int Height, int Fps, TimeSpan Duration)
{
    public long DurationMs => (long)Duration.TotalMilliseconds;

    public double AspectRatio => Height == 0 ? 0 : (double)Width / Height;
}
