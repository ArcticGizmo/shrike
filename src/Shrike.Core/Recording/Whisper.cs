using System.Diagnostics;

namespace Shrike.Core.Recording;

/// <summary>
/// Locates the whisper.cpp command-line executable that backs Shrike's local, offline transcription
/// (captions). Resolution mirrors <see cref="Ffmpeg"/>: an explicit <c>SHRIKE_WHISPER</c> override, a copy
/// bundled next to the app (the shipping path — the small binary is fetched at release time), a
/// Shrike-managed copy under <c>%LOCALAPPDATA%\Shrike\whisper</c>, then whatever is on <c>PATH</c>. Both the
/// current <c>whisper-cli.exe</c> and the older <c>main.exe</c> name are accepted. Returns null when none is
/// found, so callers degrade with a clear "install a transcription engine" message rather than crashing.
///
/// <para>Note: the transcription <b>model</b> is a separate, opt-in download (see the model store), not
/// bundled — this only finds the engine binary.</para>
/// </summary>
public static class Whisper
{
    public const string OverrideEnvVar = "SHRIKE_WHISPER";

    // whisper.cpp renamed the CLI from `main` to `whisper-cli`; accept both so either build works.
    private static readonly string[] ExeNames = ["whisper-cli.exe", "whisper.exe", "main.exe"];

    private static string? _cached;
    private static bool _probed;

    /// <summary>Full path to a whisper CLI binary, or null if none could be found.</summary>
    public static string? Locate()
    {
        if (_probed) return _cached;
        _probed = true;
        _cached = Probe();
        return _cached;
    }

    public static bool IsAvailable => Locate() is not null;

    /// <summary>Forget a cached result (used by tests that manipulate the environment).</summary>
    public static void ResetCache() { _probed = false; _cached = null; }

    private static string? Probe()
    {
        foreach (var candidate in Candidates())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (Available(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath))
            yield return overridePath;

        // Bundled next to the app (how a shipped Shrike carries its own whisper binary).
        var baseDir = AppContext.BaseDirectory;
        foreach (var name in ExeNames) yield return Path.Combine(baseDir, name);
        foreach (var name in ExeNames) yield return Path.Combine(baseDir, "whisper", name);

        // Shrike-managed copy (same folder the model store uses).
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
            foreach (var name in ExeNames) yield return Path.Combine(local, "Shrike", "whisper", name);

        // Last resort: let the OS resolve it on PATH.
        yield return "whisper-cli";
    }

    // A rooted candidate is available iff the file exists (whisper-cli's --help exit code varies by build, so
    // we don't shell out for the explicit paths). A bare PATH name is probed by launching it.
    private static bool Available(string candidate)
    {
        if (Path.IsPathRooted(candidate)) return File.Exists(candidate);
        try
        {
            using var p = Process.Start(new ProcessStartInfo(candidate, "--help")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.HasExited; // it launched — exit code is unreliable for --help across builds
        }
        catch
        {
            return false;
        }
    }
}
