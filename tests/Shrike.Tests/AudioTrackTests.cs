using Shrike.Core.Audio;

namespace Shrike.Tests;

public class AudioTrackTests
{
    private static AudioClip Clip(long start, long dur, double gainDb = 0, bool muted = false,
        long avOffset = 0, AudioOrigin origin = AudioOrigin.EditorVoiceover) => new()
    {
        SidecarPath = "a.wav",
        Format = AudioFormat.Default,
        OutputStartMs = start,
        DurationMs = dur,
        GainDb = gainDb,
        Muted = muted,
        AvOffsetMs = avOffset,
        Origin = origin,
    };

    [Fact]
    public void Clips_are_sorted_by_effective_start()
    {
        var track = new AudioTrack([Clip(2000, 500), Clip(0, 500), Clip(1000, 500)]);
        Assert.Equal(new long[] { 0, 1000, 2000 }, track.Clips.Select(c => c.OutputStartMs));
    }

    [Fact]
    public void Empty_track_reports_empty_and_zero_duration()
    {
        Assert.True(AudioTrack.Empty.IsEmpty);
        Assert.Equal(0, AudioTrack.Empty.DurationMs);
        Assert.False(AudioTrack.Empty.HasAudibleContent);
    }

    [Fact]
    public void Duration_is_the_latest_effective_end()
    {
        var track = new AudioTrack([Clip(0, 1000), Clip(500, 2000)]); // ends 1000 and 2500
        Assert.Equal(2500, track.DurationMs);
    }

    [Fact]
    public void ActiveAt_returns_overlapping_clips_with_linear_gain()
    {
        var track = new AudioTrack([Clip(0, 1000, gainDb: 0), Clip(500, 1000, gainDb: -6.0206)]);
        var active = track.ActiveAt(700); // both cover 700ms

        Assert.Equal(2, active.Count);
        Assert.Contains(active, a => Math.Abs(a.Gain - 1.0) < 1e-6);   // 0 dB
        Assert.Contains(active, a => Math.Abs(a.Gain - 0.5) < 1e-3);   // -6 dB ~ 0.5
    }

    [Fact]
    public void ActiveAt_excludes_muted_clips()
    {
        var track = new AudioTrack([Clip(0, 1000, muted: true)]);
        Assert.Empty(track.ActiveAt(500));
        Assert.False(track.HasAudibleContent);
    }

    [Fact]
    public void Coverage_is_half_open()
    {
        var c = Clip(1000, 500); // covers [1000, 1500)
        Assert.False(c.CoversOutput(999));
        Assert.True(c.CoversOutput(1000));
        Assert.True(c.CoversOutput(1499));
        Assert.False(c.CoversOutput(1500));
    }

    [Fact]
    public void Av_offset_shifts_effective_placement_and_clamps_at_zero()
    {
        Assert.Equal(1100, Clip(1000, 500, avOffset: 100).EffectiveStartMs);
        Assert.Equal(0, Clip(1000, 500, avOffset: -5000).EffectiveStartMs); // clamped, not negative
    }
}
