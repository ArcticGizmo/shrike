namespace Shrike.Core;

/// <summary>
/// The single switch that isolates a development build from an installed release. Mirrors the same helper
/// in the sibling apps (perch, sprig): a dev instance points every per-user location at a
/// <c>"Shrike (Dev)"</c> folder and suffixes every process-global name (single-instance mutex, IPC pipe,
/// autostart Run-key value, tray tooltip) with <c>" (Dev)"</c>, so <c>dotnet run</c> is fully side-by-side
/// with the installed Shrike — separate settings, its own tray, no mutex/pipe collision — with no ceremony.
///
/// <para><see cref="IsDev"/> is <c>true</c> for a Debug build (so <c>run.bat</c> is a dev instance) and can
/// be forced either way with the <c>SHRIKE_DEV</c> environment variable (non-empty and not <c>0</c>/<c>false</c>
/// turns it on; <c>0</c>/<c>false</c> turns it off), which lets a Release build run as dev without a rebuild.</para>
///
/// <para>The install <em>directory</em> (<c>%LocalAppData%\Shrike</c>) is owned by Velopack and is never
/// channel-aware — a dev build is simply never installed, so isolation is purely a runtime concern.</para>
/// </summary>
public static class AppProfile
{
    /// <summary>True when this process should behave as a development instance (isolated identity).</summary>
    public static bool IsDev { get; } = ComputeIsDev();

    /// <summary>Per-user data folder name: <c>"Shrike"</c> for release, <c>"Shrike (Dev)"</c> for dev.</summary>
    public static string DataFolderName => IsDev ? "Shrike (Dev)" : "Shrike";

    /// <summary>Suffix for user-visible and process-global names: <c>""</c> for release, <c>" (Dev)"</c> for dev.</summary>
    public static string DisplaySuffix => IsDev ? " (Dev)" : "";

    private static bool ComputeIsDev()
    {
        var env = Environment.GetEnvironmentVariable("SHRIKE_DEV");
        if (!string.IsNullOrEmpty(env))
            return !(env == "0" || env.Equals("false", StringComparison.OrdinalIgnoreCase));
#if DEBUG
        return true;
#else
        return false;
#endif
    }
}
