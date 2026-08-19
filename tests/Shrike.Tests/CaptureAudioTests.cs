using Shrike.Core.Audio;
using Shrike.Core.Recording;

namespace Shrike.Tests;

public class CaptureAudioTests
{
    private static AudioClip Live(long sidecarOffset = 0, long dur = 10_000, double gainDb = 0) => new()
    {
        SidecarPath = "mic.wav",
        Format = AudioFormat.Default,
        OutputStartMs = 0,
        SidecarOffsetMs = sidecarOffset,
        DurationMs = dur,
        GainDb = gainDb,
        Origin = AudioOrigin.LiveCapture,
        CaptureLink = new SourceSpan(sidecarOffset, sidecarOffset + dur),
    };

    private static Segment Keep(long a, long b) => new(a, b, true);

    [Fact]
    public void No_cuts_maps_a_live_clip_back_to_itself()
    {
        var clips = CaptureAudio.RideTimeline(Live(dur: 10_000), [Keep(0, 10_000)]);
        var c = Assert.Single(clips);
        Assert.Equal(0, c.OutputStartMs);
        Assert.Equal(0, c.SidecarOffsetMs);
        Assert.Equal(10_000, c.DurationMs);
    }

    [Fact]
    public void A_middle_cut_splits_the_audio_and_closes_the_gap()
    {
        // Keep [0,3s) and [7s,10s): the audio should be two slices, back-to-back on the output.
        var clips = CaptureAudio.RideTimeline(Live(dur: 10_000), [Keep(0, 3_000), Keep(7_000, 10_000)]);
        Assert.Equal(2, clips.Count);

        Assert.Equal(0, clips[0].OutputStartMs);
        Assert.Equal(0, clips[0].SidecarOffsetMs);
        Assert.Equal(3_000, clips[0].DurationMs);

        Assert.Equal(3_000, clips[1].OutputStartMs);   // placed right after the first kept slice
        Assert.Equal(7_000, clips[1].SidecarOffsetMs);  // pulls from 7s in the sidecar
        Assert.Equal(3_000, clips[1].DurationMs);
    }

    [Fact]
    public void Total_output_duration_equals_kept_duration()
    {
        var clips = CaptureAudio.RideTimeline(Live(dur: 10_000), [Keep(1_000, 4_000), Keep(6_000, 8_000)]);
        var end = clips.Max(c => c.OutputStartMs + c.DurationMs);
        Assert.Equal(5_000, end); // 3s + 2s kept
    }

    [Fact]
    public void Gain_and_path_carry_onto_every_slice()
    {
        var clips = CaptureAudio.RideTimeline(Live(dur: 10_000, gainDb: 3.0), [Keep(0, 2_000), Keep(5_000, 7_000)]);
        Assert.All(clips, c =>
        {
            Assert.Equal("mic.wav", c.SidecarPath);
            Assert.Equal(3.0, c.GainDb, 3);
            Assert.Equal(AudioOrigin.LiveCapture, c.Origin);
        });
    }

    [Fact]
    public void A_clip_that_only_partly_covers_a_range_is_clipped()
    {
        // Sidecar covers [0,5s) but the kept range is [0,10s) — only the first 5s of audio exists.
        var clips = CaptureAudio.RideTimeline(Live(sidecarOffset: 0, dur: 5_000), [Keep(0, 10_000)]);
        var c = Assert.Single(clips);
        Assert.Equal(0, c.OutputStartMs);
        Assert.Equal(5_000, c.DurationMs);
    }

    [Fact]
    public void Editor_voiceover_clips_pass_through_unchanged()
    {
        var voiceover = Live() with { Origin = AudioOrigin.EditorVoiceover, OutputStartMs = 2_000, CaptureLink = null };
        var clips = CaptureAudio.RideTimeline(voiceover, [Keep(0, 1_000), Keep(5_000, 6_000)]);
        var c = Assert.Single(clips);
        Assert.Equal(2_000, c.OutputStartMs); // untouched by the cuts
    }

    [Fact]
    public void ForOutput_maps_every_clip_and_empties_stay_empty()
    {
        Assert.True(CaptureAudio.ForOutput(AudioTrack.Empty, [Keep(0, 1_000)]).IsEmpty);

        var track = new AudioTrack([Live(dur: 10_000)]);
        var mapped = CaptureAudio.ForOutput(track, [Keep(0, 2_000), Keep(8_000, 10_000)]);
        Assert.Equal(2, mapped.Clips.Count);
        Assert.Equal(4_000, mapped.DurationMs);
    }
}
