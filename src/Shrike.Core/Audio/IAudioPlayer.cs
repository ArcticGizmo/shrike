namespace Shrike.Core.Audio;

/// <summary>
/// Minimal playback seam for the mic-check "test &amp; play back" step and, later, editor scrub-synced audio.
/// Core defines it; the NAudio (<c>WasapiOut</c>) implementation lives in the adapter. The scrub-tight
/// synchronisation the editor needs (A4) will extend this — kept deliberately small until those
/// requirements are real.
/// </summary>
public interface IAudioPlayer : IDisposable
{
    /// <summary>Load a PCM WAV file for playback, replacing anything currently loaded.</summary>
    void Load(string wavPath);

    /// <summary>Start (or resume) playback from <see cref="Position"/>.</summary>
    void Play();

    /// <summary>Pause, keeping <see cref="Position"/>.</summary>
    void Pause();

    /// <summary>Stop and reset <see cref="Position"/> to zero.</summary>
    void Stop();

    /// <summary>Playback head. Settable to seek.</summary>
    TimeSpan Position { get; set; }

    /// <summary>Total length of the loaded clip.</summary>
    TimeSpan Duration { get; }

    /// <summary>True while audio is actively playing.</summary>
    bool IsPlaying { get; }
}
