namespace Shrike.Core;

/// <summary>
/// Resolves Shrike's per-user <b>local</b> working locations (under <c>%LOCALAPPDATA%</c>, which does not
/// roam), isolated per profile via <see cref="AppProfile.DataFolderName"/> so a dev build never mixes with
/// the installed release. Recordings and their <c>*.track.json</c> sidecars live here rather than
/// <c>%TEMP%</c>, so they survive OS temp cleanup and can be re-opened / re-exported later.
/// </summary>
public static class AppStorage
{
    /// <summary>The per-user local data root: <c>%LOCALAPPDATA%\Shrike</c> (or <c>Shrike (Dev)</c> for a dev build).</summary>
    public static string LocalRoot => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppProfile.DataFolderName);

    /// <summary>
    /// Working folder for in-flight recordings and their sidecars, created on demand. These are the
    /// high-quality source MP4s the editor exports from — kept until the user is done with them (a size
    /// cap / eviction is a future refinement; for now they accumulate here).
    /// </summary>
    public static string RecordingsDirectory()
    {
        var dir = System.IO.Path.Combine(LocalRoot, "recordings");
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The track sidecar path for a recording (<c>name.mp4</c> → <c>name.track.json</c>). One place
    /// owns this convention so the writer and the retention sweep never disagree.</summary>
    public static string SidecarFor(string recordingPath) =>
        System.IO.Path.ChangeExtension(recordingPath, ".track.json");

    /// <summary>The edit-document path for a recording (<c>name.mp4</c> → <c>name.edit.json</c>). Holds the
    /// user's authored, non-destructive edit state (zoom events today, more lanes later) — a different
    /// lifecycle from the capture-time <c>*.track.json</c>, so it's a separate sidecar.</summary>
    public static string EditDocFor(string recordingPath) =>
        System.IO.Path.ChangeExtension(recordingPath, ".edit.json");

    /// <summary>The microphone audio sidecar for a recording (<c>name.mp4</c> → <c>name.mic.wav</c>).
    /// Captured live during recording; consumed by the editor/export. Same one-owner convention as the
    /// other sidecars so the retention sweep can find and evict it.</summary>
    public static string MicWavFor(string recordingPath) =>
        System.IO.Path.ChangeExtension(recordingPath, ".mic.wav");

    /// <summary>The system-sound (loopback) audio sidecar (<c>name.mp4</c> → <c>name.sys.wav</c>).</summary>
    public static string SystemWavFor(string recordingPath) =>
        System.IO.Path.ChangeExtension(recordingPath, ".sys.wav");

    /// <summary>The first in-editor voiceover sidecar (<c>name.mp4</c> → <c>name.vo.wav</c>). Kept for
    /// back-compat and as the first take's name; further takes get <c>.vo2.wav</c>, <c>.vo3.wav</c>, … via
    /// <see cref="NewVoiceoverWavFor"/>.</summary>
    public static string VoiceoverWavFor(string recordingPath) =>
        System.IO.Path.ChangeExtension(recordingPath, ".vo.wav");

    /// <summary>Pick a fresh voiceover sidecar path for a new take, so multiple takes coexist rather than
    /// overwriting one file: <c>name.vo.wav</c> if free, else <c>name.vo2.wav</c>, <c>name.vo3.wav</c>, … The
    /// first free name on disk is returned (a deleted take's clip is dropped from the edit but its file lingers,
    /// so this never clobbers a take still referenced elsewhere).</summary>
    public static string NewVoiceoverWavFor(string recordingPath)
    {
        var first = VoiceoverWavFor(recordingPath);
        if (!System.IO.File.Exists(first)) return first;
        for (var n = 2; ; n++)
        {
            var candidate = System.IO.Path.ChangeExtension(recordingPath, $".vo{n}.wav");
            if (!System.IO.File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>The audio sidecar suffixes, for the retention sweep to evict alongside a recording.</summary>
    public static IReadOnlyList<string> AudioSidecarSuffixes { get; } = [".mic.wav", ".sys.wav", ".vo.wav"];

    /// <summary>Folder holding the whisper.cpp engine and its downloaded transcription models
    /// (<c>%LOCALAPPDATA%\Shrike\whisper</c>). The engine binary may be bundled next to the app instead;
    /// models are always an opt-in, in-app download (never shipped in the installer) and land under
    /// <see cref="WhisperModelsDirectory"/>.</summary>
    public static string WhisperDirectory()
    {
        var dir = System.IO.Path.Combine(LocalRoot, "whisper");
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Folder for downloaded whisper transcription models (<c>…\whisper\models</c>), created on demand.</summary>
    public static string WhisperModelsDirectory()
    {
        var dir = System.IO.Path.Combine(WhisperDirectory(), "models");
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }
}
