using System.Runtime.Versioning;
using Microsoft.Win32;
using Shrike.Core;

namespace Shrike.App.Native;

/// <summary>
/// Launch-at-login, via the per-user <c>Run</c> registry key (no admin rights, no scheduled task). Opt-in
/// only — driven solely by the settings toggle, which is off by default (a locked review decision). Uses
/// the current executable path, so it keeps working after a Velopack update swaps the binary.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // "Shrike (Dev)" for a dev build, so toggling autostart there never overwrites the release's entry.
    private static readonly string ValueName = "Shrike" + AppProfile.DisplaySuffix;

    /// <summary>Add or remove the login entry to match <paramref name="enabled"/>. Best-effort.</summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;

            if (enabled)
            {
                if (Environment.ProcessPath is { } exe)
                    key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* registry unavailable — non-fatal */ }
    }

    /// <summary>True if a login entry is currently present.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch { return false; }
    }
}
