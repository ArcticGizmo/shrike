using Shrike.Core.Recording;
using static Shrike.Core.Recording.HardwareEncoders;

namespace Shrike.Tests;

public class ExportCommandTests
{
    private static readonly RecordingSource FullHd60 = new("C:\\in.mp4", 1920, 1080, 60, TimeSpan.FromSeconds(10));

    private static ExportProfile Preset(string name) => ExportProfile.Presets.First(p => p.Name == name);

    private static string Join(ExportCommand c) => string.Join(" ", c.Arguments);

    [Fact]
    public void H264_single_range_caps_fps_without_scaling_when_height_already_fits()
    {
        var cmd = ExportCommand.Build(FullHd60,
            new[] { new Segment(0, 10_000, true) }, Preset("Most compatible"), null, "out.mp4");

        var s = Join(cmd);
        Assert.Contains("libx264", s);
        Assert.Contains("-crf 23", s);
        Assert.Contains("fps=30", s);
        Assert.DoesNotContain("scale=", s);
        Assert.Contains("-map [vout]", s);
        Assert.True(cmd.IsReencode);
        Assert.Equal((1920, 1080, 30), (cmd.TargetWidth, cmd.TargetHeight, cmd.TargetFps));
    }

    [Fact]
    public void Slack_small_downscales_concats_and_tags_hevc()
    {
        var cmd = ExportCommand.Build(FullHd60,
            new[] { new Segment(0, 3_000, true), new Segment(7_000, 10_000, true) },
            Preset("Slack-small"), null, "out.mp4");

        var s = Join(cmd);
        Assert.Contains("concat=n=2:v=1:a=0", s);
        Assert.Contains("scale=-2:720", s);
        Assert.Contains("libx265", s);
        Assert.Contains("hvc1", s);
        Assert.Equal(1280, cmd.TargetWidth);   // 1920 * 720/1080, kept even
        Assert.Equal(720, cmd.TargetHeight);
    }

    [Fact]
    public void Source_single_range_stream_copies_without_reencoding()
    {
        var cmd = ExportCommand.Build(FullHd60,
            new[] { new Segment(1_000, 5_000, true) }, Preset("Source"), null, "out.mp4");

        var s = Join(cmd);
        Assert.Contains("-ss 1", s);
        Assert.Contains("-to 5", s);
        Assert.Contains("-c copy", s);
        Assert.DoesNotContain("filter_complex", s);
        Assert.False(cmd.IsReencode);
    }

    [Fact]
    public void Source_multi_range_falls_back_to_near_lossless_reencode()
    {
        var cmd = ExportCommand.Build(FullHd60,
            new[] { new Segment(0, 2_000, true), new Segment(5_000, 8_000, true) },
            Preset("Source"), null, "out.mp4");

        var s = Join(cmd);
        Assert.Contains("concat=n=2", s);
        Assert.Contains("libx264", s);
        Assert.Contains("-crf 18", s);
        Assert.True(cmd.IsReencode);
    }

    [Fact]
    public void Gif_builds_a_palette_graph_and_no_video_codec()
    {
        var cmd = ExportCommand.Build(FullHd60,
            new[] { new Segment(0, 4_000, true) }, Preset("GIF"), null, "out.gif");

        var s = Join(cmd);
        Assert.Contains("fps=15", s);
        Assert.Contains("scale=-2:480", s);
        Assert.Contains("split", s);
        Assert.Contains("palettegen", s);
        Assert.Contains("paletteuse", s);
        Assert.DoesNotContain("-c:v", s);
        Assert.Equal(".gif", Preset("GIF").Extension);
    }

    [Fact]
    public void Webp_uses_libwebp()
    {
        var cmd = ExportCommand.Build(FullHd60,
            new[] { new Segment(0, 4_000, true) }, Preset("WebP"), null, "out.webp");

        var s = Join(cmd);
        Assert.Contains("-c:v libwebp", s);
        Assert.Contains("-loop 0", s);
    }

    [Fact]
    public void Hardware_encoder_substitutes_for_the_software_codec()
    {
        var qsv = new HwEncoder("hevc_qsv", ExportCodec.H265, "-global_quality");
        var cmd = ExportCommand.Build(FullHd60,
            new[] { new Segment(0, 10_000, true) }, Preset("Slack-small"), qsv, "out.mp4");

        var s = Join(cmd);
        Assert.Contains("hevc_qsv", s);
        Assert.Contains("-global_quality 30", s);
        Assert.Contains("hvc1", s);
        Assert.DoesNotContain("libx265", s);
    }

    [Fact]
    public void Never_upscales_beyond_the_source_height()
    {
        var small = new RecordingSource("s.mp4", 640, 360, 30, TimeSpan.FromSeconds(5));
        var cmd = ExportCommand.Build(small,
            new[] { new Segment(0, 5_000, true) }, Preset("Slack-small"), null, "out.mp4");  // wants 720p

        Assert.Equal(360, cmd.TargetHeight);            // clamped to source
        Assert.DoesNotContain("scale=", Join(cmd));     // no scaling when already below the cap
    }

    [Fact]
    public void Empty_ranges_is_an_error()
    {
        Assert.Throws<ArgumentException>(() =>
            ExportCommand.Build(FullHd60, Array.Empty<Segment>(), Preset("Slack-small"), null, "out.mp4"));
    }
}
