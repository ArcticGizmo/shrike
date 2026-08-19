using Shrike.Core.Audio;

namespace Shrike.Core.Recording;

/// <summary>
/// A rough pre-export file-size estimate — the number under the footprint dial. It's a bits-per-pixel
/// model scaled by the CRF quality knob, so it moves the right way as you change resolution, fps, codec
/// and quality. Screen content compresses better than this assumes, so the estimate skews slightly high;
/// it's advisory, meant to let you compare presets before committing, not a guarantee.
/// </summary>
public static class ExportSize
{
    // Baseline bits-per-pixel-per-frame at each codec's reference CRF, for typical screen content.
    private const double H264BppAtCrf23 = 0.10;
    private const double H265BppAtCrf28 = 0.05;

    // AAC audio bitrate the mux targets (see ExportCommand.AudioCodecArgs).
    private const double AudioBitsPerSecond = 160_000;

    /// <summary>Estimated output size in bytes, or null when it can't be modelled (e.g. stream-copy). When an
    /// audible <paramref name="audio"/> track is present on a format that carries audio, its AAC stream is
    /// added on top of the video estimate.</summary>
    public static long? EstimateBytes(ExportProfile profile, int width, int height, int fps, long keptDurationMs,
        long? sourceFileBytes = null, long? sourceDurationMs = null, AudioTrack? audio = null)
    {
        var seconds = keptDurationMs / 1000.0;
        if (seconds <= 0 || width <= 0 || height <= 0 || fps <= 0) return 0;

        long? video = profile.Codec switch
        {
            // No re-encode: same bitrate as the source, scaled by how much you kept.
            ExportCodec.Copy => sourceFileBytes is { } bytes && sourceDurationMs is > 0
                ? (long)(bytes * (keptDurationMs / (double)sourceDurationMs))
                : null,
            ExportCodec.H264 => Bpp(H264BppAtCrf23, 23, profile.Crf, width, height, fps, seconds),
            ExportCodec.H265 => Bpp(H265BppAtCrf28, 28, profile.Crf, width, height, fps, seconds),
            // Lossy animated image — in the H.265 ballpark for these clips.
            ExportCodec.WebP => Bpp(0.06, 28, 28, width, height, fps, seconds),
            // Palette + LZW: much heavier per pixel, and largely quality-independent.
            ExportCodec.Gif => (long)(0.20 * width * height * fps * seconds),
            _ => null,
        };

        if (video is { } v && audio is { IsEmpty: false }
            && profile.Codec is ExportCodec.H264 or ExportCodec.H265 or ExportCodec.Copy)
        {
            var audioSeconds = Math.Min(audio.DurationMs, keptDurationMs) / 1000.0;
            return v + (long)(AudioBitsPerSecond / 8.0 * audioSeconds);
        }
        return video;
    }

    // Halving CRF steps of 6 roughly double the bitrate; that's the knob's shape.
    private static long Bpp(double baseBpp, int refCrf, int crf, int w, int h, int fps, double seconds)
    {
        var bpp = baseBpp * Math.Pow(2, (refCrf - crf) / 6.0);
        var bits = bpp * w * h * fps * seconds;
        return (long)(bits / 8);
    }
}
