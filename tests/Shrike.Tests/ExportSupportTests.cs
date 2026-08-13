using Shrike.Core.Recording;

namespace Shrike.Tests;

public class ExportSupportTests
{
    // ---- HardwareEncoders.ParseEncoderList ----

    [Fact]
    public void Parses_known_hardware_encoders_out_of_the_ffmpeg_list()
    {
        const string list =
            " V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)\n" +
            " V....D hevc_qsv             HEVC (Intel Quick Sync Video acceleration) (codec hevc)\n" +
            " V....D libx265              libx265 H.265 / HEVC (codec hevc)\n";

        var found = HardwareEncoders.ParseEncoderList(list);
        Assert.Contains("hevc_qsv", found);
        Assert.Contains("h264_nvenc", found);
        Assert.DoesNotContain("libx265", found);   // software, not in our hardware table
        Assert.DoesNotContain("hevc_amf", found);
    }

    // ---- MediaProbe.ParseDuration ----

    [Fact]
    public void Parses_duration_from_the_ffmpeg_banner()
    {
        const string banner = "  Duration: 00:01:03.50, start: 0.000000, bitrate: 4200 kb/s";
        var d = MediaProbe.ParseDuration(banner);
        Assert.NotNull(d);
        Assert.Equal(63.5, d!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void Returns_null_when_no_duration_present()
        => Assert.Null(MediaProbe.ParseDuration("no timing here"));

    // ---- ExportSize ----

    [Fact]
    public void Hevc_estimates_smaller_than_h264_for_the_same_clip()
    {
        var h264 = ExportSize.EstimateBytes(
            ExportProfile.Presets.First(p => p.Codec == ExportCodec.H264), 1280, 720, 30, 10_000);
        var h265 = ExportSize.EstimateBytes(
            ExportProfile.Presets.First(p => p.Name == "Slack-small"), 1280, 720, 30, 10_000);
        Assert.True(h265 < h264);
    }

    [Fact]
    public void Lower_crf_estimates_a_bigger_file()
    {
        var hi = ExportProfile.Presets.First(p => p.Name == "Most compatible");         // crf 23
        var lo = hi with { Crf = 17 };
        Assert.True(ExportSize.EstimateBytes(lo, 1280, 720, 30, 10_000)
                  > ExportSize.EstimateBytes(hi, 1280, 720, 30, 10_000));
    }

    [Fact]
    public void Stream_copy_estimate_is_the_kept_fraction_of_the_source()
    {
        var source = ExportProfile.Presets.First(p => p.Codec == ExportCodec.Copy);
        var bytes = ExportSize.EstimateBytes(source, 1920, 1080, 60, keptDurationMs: 5_000,
            sourceFileBytes: 1_000_000, sourceDurationMs: 10_000);
        Assert.Equal(500_000, bytes);
    }

    [Fact]
    public void Stream_copy_without_source_facts_cannot_estimate()
    {
        var source = ExportProfile.Presets.First(p => p.Codec == ExportCodec.Copy);
        Assert.Null(ExportSize.EstimateBytes(source, 1920, 1080, 60, 5_000));
    }
}
