namespace Shrike.Core.Audio;

/// <summary>One active clip at a query time, with the linear gain to apply — the atom the mix is built from.</summary>
public readonly record struct ActiveAudio(AudioClip Clip, double Gain);

/// <summary>
/// The ordered set of <see cref="AudioClip"/>s that make up a recording's narration/audio. Immutable and
/// UI-free: the editor builds a new track on every change, the exporter turns it into an ffmpeg mix graph,
/// and the preview asks <see cref="ActiveAt"/> what to play. Clips are held sorted by effective output start
/// so overlaps and ordering are deterministic. An empty track means "no audio" — the export path then stays
/// byte-for-byte today's silent transcode.
/// </summary>
public sealed class AudioTrack
{
    /// <summary>Clips in effective-output-start order.</summary>
    public IReadOnlyList<AudioClip> Clips { get; }

    public AudioTrack(IEnumerable<AudioClip> clips)
    {
        Clips = clips
            .OrderBy(c => c.EffectiveStartMs)
            .ThenBy(c => c.EffectiveEndMs)
            .ToArray();
    }

    public static AudioTrack Empty { get; } = new([]);

    /// <summary>No clips to mix or export.</summary>
    public bool IsEmpty => Clips.Count == 0;

    /// <summary>Output-timeline length: the latest clip end (0 when empty).</summary>
    public long DurationMs => Clips.Count == 0 ? 0 : Clips.Max(c => c.EffectiveEndMs);

    /// <summary>Whether any clip actually contributes signal (a track of only muted clips exports silent).</summary>
    public bool HasAudibleContent => Clips.Any(c => !c.Muted && c.DurationMs > 0);

    /// <summary>Every clip covering output time <paramref name="outputMs"/>, with its linear gain. Muted clips
    /// are excluded (gain 0 carries no signal). Used by the preview to decide what to sound at the playhead
    /// and by tests; the exporter drives the whole track through a filter graph instead.</summary>
    public IReadOnlyList<ActiveAudio> ActiveAt(long outputMs)
    {
        List<ActiveAudio>? active = null;
        foreach (var c in Clips)
        {
            if (c.Muted || !c.CoversOutput(outputMs)) continue;
            (active ??= []).Add(new ActiveAudio(c, c.LinearGain));
        }
        return (IReadOnlyList<ActiveAudio>?)active ?? [];
    }
}
