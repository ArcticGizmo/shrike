using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Shrike.App.Updates;

/// <summary>Outcome of an update check.</summary>
public enum UpdateAvailability
{
    /// <summary>No feed configured, or the app wasn't installed via Velopack (e.g. a dev build).</summary>
    NotApplicable,
    /// <summary>Already on the latest release.</summary>
    UpToDate,
    /// <summary>A newer release is available.</summary>
    Available,
    /// <summary>The check failed (offline, bad feed, …). Never fatal.</summary>
    Failed,
}

/// <summary>The result of an update check, plus the Velopack handles needed to apply it.</summary>
public sealed class UpdateCheckResult
{
    public required UpdateAvailability Availability { get; init; }
    public string? CurrentVersion { get; init; }
    public string? AvailableVersion { get; init; }

    internal UpdateManager? Manager { get; init; }
    internal UpdateInfo? Info { get; init; }
}

/// <summary>
/// Checks a release feed for a newer version and (from the About window) applies it. Mirrors sprig: the
/// feed defaults to the project's GitHub Releases (<see cref="DefaultFeedUrl"/>); <c>SHRIKE_UPDATE_FEED</c>
/// (a folder path or URL) overrides it for testing against a local release folder. When the app wasn't
/// installed via Velopack (e.g. a dev build), the check is a harmless no-op. Every failure is swallowed —
/// a flaky feed must never block launch.
/// </summary>
public static class UpdateChecker
{
    public const string FeedEnvVar = "SHRIKE_UPDATE_FEED";

    // The GitHub Releases feed. Harmless until the repo is created: the check is a no-op on dev builds
    // (not Velopack-installed) and swallows any failure on installed ones.
    public const string DefaultFeedUrl = "https://github.com/ArcticGizmo/shrike";

    /// <summary>Full-detail check used by the About window. Never throws.</summary>
    public static async Task<UpdateCheckResult> CheckDetailedAsync()
    {
        var feed = Environment.GetEnvironmentVariable(FeedEnvVar);
        try
        {
            var manager = string.IsNullOrWhiteSpace(feed)
                ? new UpdateManager(new GithubSource(DefaultFeedUrl, accessToken: null, prerelease: false))
                : new UpdateManager(feed);
            if (!manager.IsInstalled)
                return new UpdateCheckResult { Availability = UpdateAvailability.NotApplicable };

            var current = manager.CurrentVersion?.ToString();
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
                return new UpdateCheckResult { Availability = UpdateAvailability.UpToDate, CurrentVersion = current };

            return new UpdateCheckResult
            {
                Availability = UpdateAvailability.Available,
                CurrentVersion = current,
                AvailableVersion = update.TargetFullRelease.Version.ToString(),
                Manager = manager,
                Info = update,
            };
        }
        catch
        {
            return new UpdateCheckResult { Availability = UpdateAvailability.Failed };
        }
    }

    /// <summary>Download + install the update and restart. Does not return on success.</summary>
    public static async Task ApplyAsync(UpdateCheckResult result)
    {
        if (result is not { Availability: UpdateAvailability.Available, Manager: { } manager, Info: { } info })
            return;

        await manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
        manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
    }

    /// <summary>A one-line notice when a newer release exists, else null. Used by the launch check.</summary>
    public static async Task<string?> CheckAsync()
    {
        var result = await CheckDetailedAsync().ConfigureAwait(false);
        return result.Availability == UpdateAvailability.Available
            ? $"Update available: v{result.AvailableVersion} — you have v{result.CurrentVersion}"
            : null;
    }
}
