using System.Diagnostics;

namespace Shrike.Core.Recording;

/// <summary>
/// Which of ffmpeg's hardware video encoders this machine actually exposes. Detected once by asking
/// ffmpeg for its encoder list (<c>-encoders</c>) and matching the names we care about; export prefers a
/// hardware encoder (Intel QSV / NVIDIA NVENC / AMD AMF) over the software <c>libx264</c>/<c>libx265</c>
/// path when one is present, because it's far faster and cooler. Software is always the fallback, so
/// export never depends on hardware being there.
/// </summary>
public static class HardwareEncoders
{
    /// <summary>An ffmpeg hardware encoder we know how to drive, newest/preferred first.</summary>
    public sealed record HwEncoder(string Name, ExportCodec Codec, string QualityFlag)
    {
        /// <summary>Args that set the quality target for this encoder at a given CRF-equivalent value.</summary>
        public IEnumerable<string> QualityArgs(int crf) => new[] { QualityFlag, crf.ToString() };
    }

    // In preference order. QSV first (this dev box's Intel Arc), then NVENC, then AMF.
    private static readonly HwEncoder[] Hevc =
    {
        new("hevc_qsv", ExportCodec.H265, "-global_quality"),
        new("hevc_nvenc", ExportCodec.H265, "-cq"),
        new("hevc_amf", ExportCodec.H265, "-qp_p"),
    };

    private static readonly HwEncoder[] Avc =
    {
        new("h264_qsv", ExportCodec.H264, "-global_quality"),
        new("h264_nvenc", ExportCodec.H264, "-cq"),
        new("h264_amf", ExportCodec.H264, "-qp_p"),
    };

    private static HashSet<string>? _available;

    /// <summary>The best available hardware encoder for a codec, or null to fall back to software.</summary>
    public static HwEncoder? Best(ExportCodec codec, string? ffmpegPath = null)
    {
        var set = Available(ffmpegPath);
        var table = codec switch
        {
            ExportCodec.H265 => Hevc,
            ExportCodec.H264 => Avc,
            _ => Array.Empty<HwEncoder>(),
        };
        return table.FirstOrDefault(e => set.Contains(e.Name));
    }

    /// <summary>Names of hardware encoders ffmpeg reports as built in. Cached after first probe.</summary>
    public static HashSet<string> Available(string? ffmpegPath = null)
    {
        if (_available is not null) return _available;
        var exe = ffmpegPath ?? Ffmpeg.Locate();
        _available = exe is null ? new HashSet<string>() : Probe(exe);
        return _available;
    }

    /// <summary>Forget the cached probe (tests).</summary>
    public static void ResetCache() => _available = null;

    /// <summary>Parse the encoder names we recognise out of <c>ffmpeg -encoders</c>. Public for headless tests.</summary>
    public static HashSet<string> ParseEncoderList(string encodersOutput)
    {
        var known = Hevc.Concat(Avc).Select(e => e.Name).ToHashSet();
        var found = new HashSet<string>();
        foreach (var name in known)
            // Lines look like: " V....D hevc_qsv    HEVC (Intel Quick Sync Video acceleration) (codec hevc)"
            if (encodersOutput.Contains(' ' + name + ' ') || encodersOutput.Contains(' ' + name + '\n'))
                found.Add(name);
        return found;
    }

    private static HashSet<string> Probe(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "-hide_banner -encoders")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (p is null) return new HashSet<string>();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return ParseEncoderList(output);
        }
        catch
        {
            return new HashSet<string>();
        }
    }
}
