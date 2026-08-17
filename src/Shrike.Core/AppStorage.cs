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
}
