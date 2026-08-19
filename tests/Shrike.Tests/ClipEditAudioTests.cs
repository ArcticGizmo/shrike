using Shrike.Core.Audio;
using Shrike.Core.Recording;

namespace Shrike.Tests;

public class ClipEditAudioTests
{
    private static AudioClip Clip(long start, long dur, string path = "narration.wav") => new()
    {
        SidecarPath = path,
        Format = AudioFormat.Default,
        OutputStartMs = start,
        DurationMs = dur,
        SidecarOffsetMs = 250,
        GainDb = 2.0,
        AvOffsetMs = -30,
        Origin = AudioOrigin.LiveCapture,
        CaptureLink = new SourceSpan(start, start + dur),
    };

    [Fact]
    public void Round_trips_an_audio_track_through_json()
    {
        var edit = new ClipEdit(EffectTrack.Empty, new AudioTrack([Clip(2_000, 3_000)]));
        var back = ClipEdit.FromJson(edit.ToJson());

        var c = Assert.Single(back.Audio.Clips);
        Assert.Equal("narration.wav", c.SidecarPath);
        Assert.Equal(2_000, c.OutputStartMs);
        Assert.Equal(3_000, c.DurationMs);
        Assert.Equal(250, c.SidecarOffsetMs);
        Assert.Equal(2.0, c.GainDb, 3);
        Assert.Equal(-30, c.AvOffsetMs);
        Assert.Equal(AudioOrigin.LiveCapture, c.Origin);
        Assert.Equal(new SourceSpan(2_000, 5_000), c.CaptureLink);
        Assert.Equal(AudioFormat.Default, c.Format);
    }

    [Fact]
    public void Audio_and_effects_coexist_in_one_document()
    {
        var effects = new EffectTrack([new RippleEffect(0, 5_000)]);
        var edit = new ClipEdit(effects, new AudioTrack([Clip(0, 4_000)]));
        var back = ClipEdit.FromJson(edit.ToJson());

        Assert.Single(back.Effects.OfKind<RippleEffect>());
        Assert.Single(back.Audio.Clips);
    }

    [Fact]
    public void Audio_only_edit_is_not_empty_and_persists()
    {
        var edit = new ClipEdit(EffectTrack.Empty, new AudioTrack([Clip(0, 1_000)]));
        Assert.False(edit.IsEmpty);

        var path = Path.Combine(Path.GetTempPath(), $"shrike-v3-{Guid.NewGuid():N}.edit.json");
        try
        {
            edit.Save(path);
            Assert.True(File.Exists(path));
            Assert.Single(ClipEdit.Load(path).Audio.Clips);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Effect_track_without_audio_has_an_empty_audio_track()
    {
        var back = ClipEdit.FromJson(new ClipEdit(new EffectTrack([new RippleEffect(0, 1_000)])).ToJson());
        Assert.True(back.Audio.IsEmpty);
    }

    [Fact]
    public void A_v2_document_loads_with_no_audio()
    {
        // A v2 file (no Audio field) must still open — audio simply reads as empty.
        const string v2Json = """{"V":2,"Ripple":[{"Start":0,"End":1000}]}""";
        var back = ClipEdit.FromJson(v2Json);
        Assert.Single(back.Effects.OfKind<RippleEffect>());
        Assert.True(back.Audio.IsEmpty);
    }

    [Fact]
    public void Malformed_audio_clips_are_dropped_on_parse()
    {
        // Zero-length and path-less clips are nonsense; the parser filters them.
        const string json = """
        {"V":3,"Audio":[
          {"Path":"ok.wav","Dur":1000,"Start":0},
          {"Path":"","Dur":1000},
          {"Path":"empty.wav","Dur":0}
        ]}
        """;
        var back = ClipEdit.FromJson(json);
        var c = Assert.Single(back.Audio.Clips);
        Assert.Equal("ok.wav", c.SidecarPath);
    }

    [Fact]
    public void Muted_audio_only_edit_still_persists()
    {
        // Even a muted clip is authored state worth keeping (it's not "no edit").
        var muted = Clip(0, 1_000) with { Muted = true };
        var edit = new ClipEdit(EffectTrack.Empty, new AudioTrack([muted]));
        Assert.False(edit.IsEmpty);
        Assert.True(ClipEdit.FromJson(edit.ToJson()).Audio.Clips[0].Muted);
    }
}
