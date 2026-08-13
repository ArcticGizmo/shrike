using System.Diagnostics;

namespace Shrike.Core.Recording;

/// <summary>
/// Locates the <c>ffmpeg</c> executable that backs Shrike's video encoding. Resolution order:
/// an explicit <c>SHRIKE_FFMPEG</c> override, a copy bundled next to the app (the shipping path),
/// the winget shim, then whatever is on <c>PATH</c>. Returns null when none is found, so callers can
/// degrade gracefully with a clear message rather than crashing.
/// </summary>
public static class Ffmpeg
{
    public const string OverrideEnvVar = "SHRIKE_FFMPEG";

    private static string? _cached;
    private static bool _probed;

    /// <summary>Full path to a working ffmpeg, or null if none could be found.</summary>
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
            if (Runs(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath))
            yield return overridePath;

        // Bundled next to the app (how a shipped Shrike carries its own ffmpeg).
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "ffmpeg.exe");
        yield return Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe");

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            // Shrike-managed copy (where a first-run provisioner places it).
            yield return Path.Combine(local, "Shrike", "ffmpeg", "ffmpeg.exe");
            // winget's shim directory (not always on the current process's PATH).
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        }

        // Last resort: let the OS resolve it on PATH.
        yield return "ffmpeg";
    }

    private static bool Runs(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "-hide_banner -version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
