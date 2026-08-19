using Shrike.Core.Recording;

namespace Shrike.Tests;

public class TimelineTests
{
    private static Timeline Make(long durationMs = 10_000) => new(durationMs);

    [Fact]
    public void Starts_as_one_kept_span_covering_the_whole_source()
    {
        var t = Make();
        Assert.Single(t.Segments);
        Assert.Equal(new Segment(0, 10_000, true), t.Segments[0]);
        Assert.Equal(10_000, t.KeptDurationMs);
        Assert.True(t.HasKeptContent);
    }

    [Fact]
    public void Cutting_the_middle_yields_keep_cut_keep()
    {
        var t = Make();
        t.Cut(3_000, 7_000);

        Assert.Equal(3, t.Segments.Count);
        Assert.Equal(new Segment(0, 3_000, true), t.Segments[0]);
        Assert.Equal(new Segment(3_000, 7_000, false), t.Segments[1]);
        Assert.Equal(new Segment(7_000, 10_000, true), t.Segments[2]);
        Assert.Equal(6_000, t.KeptDurationMs);
    }

    [Fact]
    public void Adjacent_same_state_spans_merge()
    {
        var t = Make();
        t.Cut(3_000, 5_000);
        t.Cut(5_000, 7_000);   // touches the first cut — should coalesce into one 3000..7000 cut

        Assert.Equal(3, t.Segments.Count);
        Assert.Equal(new Segment(3_000, 7_000, false), t.Segments[1]);
    }

    [Fact]
    public void Split_divides_a_span_into_two_kept_segments_that_survive()
    {
        var t = Make();
        t.Split(4_000);

        Assert.Equal(2, t.Segments.Count);
        Assert.Equal(new Segment(0, 4_000, true), t.Segments[0]);
        Assert.Equal(new Segment(4_000, 10_000, true), t.Segments[1]);
        Assert.Contains(4_000L, t.Splits);
        Assert.Equal(10_000, t.KeptDurationMs); // a split never changes what plays/exports
    }

    [Fact]
    public void Split_then_cut_one_side_marks_only_that_span()
    {
        var t = Make();
        t.Split(4_000);
        t.SetSegmentKept(2_000, kept: false); // cut the left span (contains 2000)

        Assert.Equal(2, t.Segments.Count);
        Assert.Equal(new Segment(0, 4_000, false), t.Segments[0]);
        Assert.Equal(new Segment(4_000, 10_000, true), t.Segments[1]);
        Assert.Equal(6_000, t.KeptDurationMs);
    }

    [Fact]
    public void RemoveSplit_merges_the_two_spans_back()
    {
        var t = Make();
        t.Split(4_000);
        t.RemoveSplitAt(4_100, toleranceMs: 500);

        Assert.Single(t.Segments);
        Assert.Empty(t.Splits);
        Assert.Equal(new Segment(0, 10_000, true), t.Segments[0]);
    }

    [Fact]
    public void MoveBoundary_resizes_a_cut()
    {
        var t = Make();
        t.Cut(3_000, 7_000);       // keep · cut · keep, boundaries at 3000 and 7000
        t.MoveBoundary(7_000, 6_000); // pull the cut's right edge in to 6000

        Assert.Equal(3, t.Segments.Count);
        Assert.Equal(new Segment(3_000, 6_000, false), t.Segments[1]);
        Assert.Equal(7_000, t.KeptDurationMs); // kept grew by 1000
    }

    [Fact]
    public void MoveBoundary_moves_a_split_between_two_kept_spans()
    {
        var t = Make();
        t.Split(4_000);
        t.MoveBoundary(4_000, 6_000);

        Assert.Equal(2, t.Segments.Count);
        Assert.Equal(6_000, t.Segments[0].EndMs);
        Assert.Contains(6_000L, t.Splits);
        Assert.DoesNotContain(4_000L, t.Splits);
    }

    [Fact]
    public void Restoring_a_cut_returns_to_a_single_kept_span()
    {
        var t = Make();
        t.Cut(3_000, 7_000);
        t.Keep(3_000, 7_000);

        Assert.Single(t.Segments);
        Assert.Equal(new Segment(0, 10_000, true), t.Segments[0]);
    }

    [Fact]
    public void KeepOnly_cuts_everything_outside_the_window()
    {
        var t = Make();
        t.KeepOnly(2_000, 6_000);

        Assert.Equal(new[]
        {
            new Segment(0, 2_000, false),
            new Segment(2_000, 6_000, true),
            new Segment(6_000, 10_000, false),
        }, t.Segments);
        Assert.Equal(4_000, t.KeptDurationMs);
    }

    [Fact]
    public void Delete_and_restore_segment_by_point()
    {
        var t = Make();
        t.Cut(3_000, 7_000);       // keep · cut · keep

        t.DeleteSegmentAt(1_000);  // cut the first kept span — merges with the [3000,7000) cut
        Assert.False(t.Find(1_000)!.Value.Kept);
        Assert.Equal(3_000, t.KeptDurationMs);   // only 7000..10000 remains

        // The two cuts are now one contiguous span; restoring it brings the whole front back.
        t.RestoreSegmentAt(1_000);
        Assert.True(t.Find(1_000)!.Value.Kept);
        Assert.Equal(10_000, t.KeptDurationMs);
        Assert.Single(t.Segments);
    }

    [Fact]
    public void Cutting_everything_leaves_no_kept_content()
    {
        var t = Make();
        t.Cut(0, 10_000);
        Assert.False(t.HasKeptContent);
        Assert.Equal(0, t.KeptDurationMs);
    }

    [Fact]
    public void RestoreAll_drops_every_edit()
    {
        var t = Make();
        t.Cut(1_000, 2_000);
        t.Cut(4_000, 8_000);
        t.RestoreAll();

        Assert.Single(t.Segments);
        Assert.Equal(new Segment(0, 10_000, true), t.Segments[0]);
    }

    [Fact]
    public void EditedToSource_maps_across_a_cut()
    {
        var t = Make();
        t.Cut(3_000, 7_000);   // kept: [0,3000) then [7000,10000)

        Assert.Equal(0, t.EditedToSourceMs(0));
        Assert.Equal(2_000, t.EditedToSourceMs(2_000));       // inside first kept span
        Assert.Equal(7_000, t.EditedToSourceMs(3_000));       // start of second kept span
        Assert.Equal(8_500, t.EditedToSourceMs(4_500));       // 1500ms into second span
        Assert.Equal(10_000, t.EditedToSourceMs(99_999));     // past the end → last kept frame
    }

    [Fact]
    public void SourceToEdited_is_null_inside_a_cut()
    {
        var t = Make();
        t.Cut(3_000, 7_000);

        Assert.Equal(2_000, t.SourceToEditedMs(2_000));
        Assert.Null(t.SourceToEditedMs(5_000));               // inside the cut
        Assert.Equal(3_000, t.SourceToEditedMs(7_000));       // first source ms after the cut
        Assert.Equal(4_500, t.SourceToEditedMs(8_500));
    }

    [Fact]
    public void KeptRanges_lists_playable_spans_in_order()
    {
        var t = Make();
        t.Cut(3_000, 7_000);

        Assert.Equal(new[]
        {
            new Segment(0, 3_000, true),
            new Segment(7_000, 10_000, true),
        }, t.KeptRanges);
    }

    [Fact]
    public void KeptRangesFrom_clips_the_first_span_and_drops_earlier_ones()
    {
        var t = Make();
        t.Cut(3_000, 7_000);   // kept: [0,3000) then [7000,10000)

        // From the very start: both kept spans, untouched.
        Assert.Equal(new[] { new Segment(0, 3_000, true), new Segment(7_000, 10_000, true) },
            t.KeptRangesFrom(0));

        // 1s into the edited timeline: first span clipped to source 1000.
        Assert.Equal(new[] { new Segment(1_000, 3_000, true), new Segment(7_000, 10_000, true) },
            t.KeptRangesFrom(1_000));

        // 4s in (past the 3s first span): only the second span, clipped 1s in → source 8000.
        Assert.Equal(new[] { new Segment(8_000, 10_000, true) }, t.KeptRangesFrom(4_000));

        // At/after the end: nothing left to play.
        Assert.Empty(t.KeptRangesFrom(6_000));
        Assert.Empty(t.KeptRangesFrom(99_999));
    }

    [Fact]
    public void Edits_clamp_to_the_source_bounds()
    {
        var t = Make();
        t.Cut(-5_000, 3_000);      // clamps start to 0
        Assert.Equal(new Segment(0, 3_000, false), t.Segments[0]);

        t.Keep(8_000, 50_000);     // already kept; clamps end to duration, stays a single tail span
        Assert.Equal(10_000, t.Segments[^1].EndMs);
    }

    [Fact]
    public void Zero_width_or_reversed_edits_are_noops()
    {
        var t = Make();
        t.Cut(5_000, 5_000);       // zero width
        Assert.Single(t.Segments);

        t.Cut(7_000, 4_000);       // reversed → normalised to [4000,7000)
        Assert.Equal(3, t.Segments.Count);
        Assert.Equal(new Segment(4_000, 7_000, false), t.Segments[1]);
    }

    [Fact]
    public void Changed_fires_on_edit()
    {
        var t = Make();
        var fired = 0;
        t.Changed += () => fired++;
        t.Cut(1_000, 2_000);
        t.RestoreAll();
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Rejects_nonpositive_duration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Timeline(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Timeline(-1));
    }
}
