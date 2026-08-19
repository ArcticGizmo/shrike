namespace Shrike.Core.Audio;

/// <summary>
/// Describes an interleaved PCM audio stream — the audio counterpart to <c>IFrameSource</c>'s Width/Height.
/// Capture is normalised to <see cref="Default"/> (48 kHz, 16-bit) so the mic and the system-loopback tap
/// share one format before they are written to sidecars or mixed. Only signed little-endian PCM is modelled;
/// float capture is converted to 16-bit in the adapter.
/// </summary>
public readonly record struct AudioFormat(int SampleRate, int Channels, int BitsPerSample)
{
    /// <summary>Bytes in one sample of one channel (2 for 16-bit).</summary>
    public int BytesPerSample => BitsPerSample / 8;

    /// <summary>Bytes in one interleaved frame across all channels.</summary>
    public int BlockAlign => Channels * BytesPerSample;

    /// <summary>Bytes for one second of audio.</summary>
    public int BytesPerSecond => SampleRate * BlockAlign;

    /// <summary>48 kHz, stereo, 16-bit — the shared-mode WASAPI target we normalise capture to.</summary>
    public static readonly AudioFormat Default = new(48_000, 2, 16);

    /// <summary>Duration in ms of a run of <paramref name="byteCount"/> PCM bytes in this format.</summary>
    public long BytesToMs(long byteCount) => BytesPerSecond == 0 ? 0 : byteCount * 1000L / BytesPerSecond;

    /// <summary>PCM byte length for <paramref name="ms"/> of audio, rounded down and block-aligned so the
    /// result always lands on a whole interleaved frame.</summary>
    public long MsToBytes(long ms)
    {
        if (ms <= 0 || BlockAlign == 0) return 0;
        var raw = ms * BytesPerSecond / 1000L;
        return raw - raw % BlockAlign;
    }

    /// <summary>True when the format is a usable, signed 16-bit PCM stream (what the rest of Core assumes).</summary>
    public bool IsPcm16 => BitsPerSample == 16 && Channels > 0 && SampleRate > 0;
}
