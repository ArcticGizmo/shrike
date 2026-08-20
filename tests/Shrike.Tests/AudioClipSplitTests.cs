using Shrike.Core.Audio;
using Xunit;

namespace Shrike.Tests;

public class AudioClipSplitTests
{
    private static AudioClip Clip(long start = 1000, long dur = 4000, long offset = 0, long av = 0) => new()
    {
        SidecarPath = "vo.wav",
        Format = AudioFormat.Default,
        OutputStartMs = start,
        DurationMs = dur,
        SidecarOffsetMs = offset,
        AvOffsetMs = av,
        Origin = AudioOrigin.EditorVoiceover,
    };

    [Fact]
    public void Split_in_the_middle_yields_two_adjacent_halves_covering_the_same_span()
    {
        var (left, right) = Clip(start: 1000, dur: 4000).SplitAtOutput(3000)!.Value;

        Assert.Equal(1000, left.OutputStartMs);
        Assert.Equal(2000, left.DurationMs);          // 1000..3000
        Assert.Equal(0, left.SidecarOffsetMs);

        Assert.Equal(3000, right.OutputStartMs);       // adjacent, no gap/overlap
        Assert.Equal(2000, right.DurationMs);          // 3000..5000
        Assert.Equal(2000, right.SidecarOffsetMs);     // sidecar in-point advanced by the head length

        Assert.Equal(left.EffectiveEndMs, right.EffectiveStartMs);
    }

    [Fact]
    public void Split_carries_an_existing_trim_in_point_onto_both_halves()
    {
        var (left, right) = Clip(start: 0, dur: 2000, offset: 500).SplitAtOutput(800)!.Value;

        Assert.Equal(500, left.SidecarOffsetMs);       // head keeps the original in-point
        Assert.Equal(800, left.DurationMs);
        Assert.Equal(1300, right.SidecarOffsetMs);     // 500 + 800
        Assert.Equal(1200, right.DurationMs);
    }

    [Fact]
    public void Split_point_is_measured_where_the_user_sees_it_so_the_av_offset_is_honoured()
    {
        // EffectiveStart = 1000 + 200 = 1200; a cut at output 1700 is 500ms into the clip.
        var (left, right) = Clip(start: 1000, dur: 4000, av: 200).SplitAtOutput(1700)!.Value;

        Assert.Equal(500, left.DurationMs);
        Assert.Equal(1500, right.OutputStartMs);       // 1000 + 500
        Assert.Equal(200, right.AvOffsetMs);           // nudge preserved on both halves
    }

    [Theory]
    [InlineData(1000)] // exactly the start
    [InlineData(500)]  // before the start
    [InlineData(5000)] // exactly the end
    [InlineData(6000)] // past the end
    public void Split_outside_the_span_returns_null(long at)
    {
        Assert.Null(Clip(start: 1000, dur: 4000).SplitAtOutput(at));
    }

    [Fact]
    public void Split_of_a_live_clip_divides_its_capture_link()
    {
        var clip = Clip(start: 0, dur: 4000) with
        {
            Origin = AudioOrigin.LiveCapture,
            CaptureLink = new SourceSpan(0, 4000),
        };

        var (left, right) = clip.SplitAtOutput(2500)!.Value;

        Assert.Equal(new SourceSpan(0, 2500), left.CaptureLink);
        Assert.Equal(new SourceSpan(2500, 4000), right.CaptureLink);
    }
}
