using Shrike.Core.Audio;
using Shrike.Core.Recording;

namespace Shrike.Tests;

public class ExportAudioTests
{
    private static readonly RecordingSource FullHd60 = new("C:\\in.mp4", 1920, 1080, 60, TimeSpan.FromSeconds(10));

    private static ExportProfile Preset(string name) => ExportProfile.Presets.First(p => p.Name == name);
    private static string Join(ExportCommand c) => string.Join(" ", c.Arguments);

    private static AudioClip Narration(long start, long dur, double gainDb = 0, long sidecarOffset = 0,
        string path = "narration.wav") => new()
    {
        SidecarPath = path,
        Format = AudioFormat.Default,
        OutputStartMs = start,
        DurationMs = dur,
        SidecarOffsetMs = sidecarOffset,
        GainDb = gainDb,
        Origin = AudioOrigin.EditorVoiceover,
    };

    private static AudioTrack Track(params AudioClip[] clips) => new(clips);

    [Fact]
    public void No_audio_track_leaves_the_command_untouched()
    {
        var withArg = ExportCommand.Build(FullHd60,
            new[] { new Segment(0, 10_000, true) }, Preset("Most compatible"), null, "out.mp4", audio: null);
        var without = ExportCommand.Build(FullHd60,
            new[] { new Segment(0, 10_000, true) }, Preset("Most compatible"), null, "out.mp4");

        Assert.Equal(Join(without), Join(withArg));
        Assert.DoesNotContain("-c:a", Join(withArg)); // off means off
    }

    [Fact]
    public void Empty_audio_track_adds_no_audio()
    {
        var cmd = ExportCommand.Build(FullHd60,
            new[] { new Segment(0, 10_000, true) }, Preset("Most compatible"), null, "out.mp4", AudioTrack.Empty);
        Assert.DoesNotContain("-c:a", Join(cmd));
        Assert.DoesNotContain("amix", Join(cmd));
    }

    [Fact]
    public void Single_clip_adds_input_delay_and_aac()
    {
        var cmd = ExportCommand.Build(FullHd60,
            new[] { new Segment(0, 10_000, true) }, Preset("Most compatible"), null, "out.mp4",
            Track(Narration(start: 2_000, dur: 3_000)));

        var s = Join(cmd);
        Assert.Contains("-i narration.wav", s);
        Assert.Contains("[1:a]atrim=start=0:end=3", s);      // 3s of the sidecar
        Assert.Contains("adelay=2000:all=1", s);             // placed at 2s on the output
        Assert.Contains("-map [a0]", s);
        Assert.Contains("-c:a aac", s);
        Assert.Contains("-b:a 160k", s);
        Assert.DoesNotContain("amix", s);                    // a single clip needs no mix
    }

    [Fact]
    public void Two_clips_are_mixed()
    {
        var cmd = ExportCommand.Build(FullHd60,
            new[] { new Segment(0, 10_000, true) }, Preset("Most compatible"), null, "out.mp4",
            Track(Narration(0, 4_000, path: "a.wav"), Narration(5_000, 3_000, path: "b.wav")));

        var s = Join(cmd);
        Assert.Contains("-i a.wav", s);
        Assert.Contains("-i b.wav", s);
        Assert.Contains("[a0][a1]amix=inputs=2:normalize=0[aout]", s);
        Assert.Contains("-map [aout]", s);
    }

    [Fact]
    public void Gain_becomes_a_volume_filter_and_zero_db_does_not()
    {
        var gained = Join(ExportCommand.Build(FullHd60, new[] { new Segment(0, 10_000, true) },
            Preset("Most compatible"), null, "out.mp4", Track(Narration(0, 3_000, gainDb: 6.0206))));
        Assert.Contains("volume=2", gained); // +6 dB ~ 2x

        var unity = Join(ExportCommand.Build(FullHd60, new[] { new Segment(0, 10_000, true) },
            Preset("Most compatible"), null, "out.mp4", Track(Narration(0, 3_000, gainDb: 0))));
        Assert.DoesNotContain("volume=", unity);
    }

    [Fact]
    public void Sidecar_offset_trims_from_within_the_file()
    {
        var cmd = ExportCommand.Build(FullHd60, new[] { new Segment(0, 10_000, true) },
            Preset("Most compatible"), null, "out.mp4", Track(Narration(0, 2_000, sidecarOffset: 1_000)));
        Assert.Contains("atrim=start=1:end=3", Join(cmd)); // [1s, 3s) of the sidecar
    }

    [Fact]
    public void Clip_at_zero_needs_no_delay()
    {
        var cmd = ExportCommand.Build(FullHd60, new[] { new Segment(0, 10_000, true) },
            Preset("Most compatible"), null, "out.mp4", Track(Narration(0, 3_000)));
        Assert.DoesNotContain("adelay", Join(cmd));
    }

    [Fact]
    public void Audio_forces_reencode_instead_of_stream_copy()
    {
        var cmd = ExportCommand.Build(FullHd60, new[] { new Segment(1_000, 5_000, true) },
            Preset("Source"), null, "out.mp4", Track(Narration(0, 3_000)));

        var s = Join(cmd);
        Assert.True(cmd.IsReencode);
        Assert.DoesNotContain("-c copy", s);
        Assert.Contains("-c:a aac", s);
        Assert.Contains("libx264", s); // near-lossless re-encode fallback carries the muxed audio
    }

    [Fact]
    public void Source_single_range_without_audio_still_stream_copies()
    {
        var cmd = ExportCommand.Build(FullHd60, new[] { new Segment(1_000, 5_000, true) },
            Preset("Source"), null, "out.mp4", AudioTrack.Empty);
        Assert.False(cmd.IsReencode);
        Assert.Contains("-c copy", Join(cmd));
    }

    [Fact]
    public void Gif_stays_silent_even_with_a_track()
    {
        var cmd = ExportCommand.Build(FullHd60, new[] { new Segment(0, 4_000, true) },
            Preset("GIF"), null, "out.gif", Track(Narration(0, 3_000)));
        var s = Join(cmd);
        Assert.DoesNotContain("-c:a", s);
        Assert.DoesNotContain("-i narration.wav", s);
        Assert.DoesNotContain("amix", s);
    }

    [Fact]
    public void Muted_clip_is_excluded_from_the_mux()
    {
        var muted = Narration(0, 3_000) with { Muted = true };
        var cmd = ExportCommand.Build(FullHd60, new[] { new Segment(0, 10_000, true) },
            Preset("Most compatible"), null, "out.mp4", Track(muted));
        Assert.DoesNotContain("-c:a", Join(cmd)); // nothing audible to mux
    }

    [Fact]
    public void BuildAudioMix_makes_an_audio_only_pcm_command()
    {
        var track = Track(Narration(0, 4_000, path: "a.wav"), Narration(5_000, 3_000, path: "b.wav"));
        var s = string.Join(" ", ExportCommand.BuildAudioMix(track, "mix.wav"));

        Assert.Contains("-i a.wav", s);
        Assert.Contains("-i b.wav", s);
        Assert.Contains("[0:a]", s);   // audio-only: inputs start at 0, not 1
        Assert.Contains("[1:a]", s);
        Assert.Contains("amix=inputs=2:normalize=0[aout]", s);
        Assert.Contains("-map [aout]", s);
        Assert.Contains("-c:a pcm_s16le", s);
        Assert.EndsWith("mix.wav", s);
        Assert.DoesNotContain("0:v", s); // no video
    }

    [Fact]
    public void BuildAudioMix_single_clip_uses_input_zero()
    {
        var s = string.Join(" ", ExportCommand.BuildAudioMix(Track(Narration(1_000, 2_000)), "mix.wav"));
        Assert.Contains("[0:a]atrim=start=0:end=2", s);
        Assert.Contains("adelay=1000:all=1", s);
        Assert.Contains("-map [a0]", s);
        Assert.DoesNotContain("amix", s);
    }

    [Fact]
    public void BuildAudioMix_excludes_muted_and_empty()
    {
        var track = new AudioTrack([Narration(0, 3_000) with { Muted = true }]);
        var s = string.Join(" ", ExportCommand.BuildAudioMix(track, "mix.wav"));
        Assert.DoesNotContain("-map", s);   // nothing audible → no filter/map
        Assert.DoesNotContain("-i", s);
    }

    [Fact]
    public void Estimate_adds_audio_bytes_for_h264()
    {
        var track = Track(Narration(0, 10_000)); // 10s of narration
        var withAudio = ExportSize.EstimateBytes(Preset("Most compatible"), 1280, 720, 30, 10_000, audio: track);
        var without = ExportSize.EstimateBytes(Preset("Most compatible"), 1280, 720, 30, 10_000);

        Assert.NotNull(withAudio);
        Assert.NotNull(without);
        // 10s * 160kbps / 8 = 200_000 bytes of audio added.
        Assert.Equal(200_000, withAudio!.Value - without!.Value);
    }
}
