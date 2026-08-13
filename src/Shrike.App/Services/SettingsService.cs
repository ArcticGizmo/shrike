using System.Runtime.Versioning;
using Shrike.App.Native;
using Shrike.Core.Settings;

namespace Shrike.App.Services;

/// <summary>
/// Holds the live <see cref="AppSettings"/> for the running app: loads them at startup, hands the current
/// value to whoever needs it, and — when the user saves from the settings window — persists, applies the
/// registry side-effect (autostart), and raises <see cref="Changed"/> so the rest of the app (hotkeys,
/// ring) can re-apply. One instance, created once in <c>App</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SettingsService
{
    private readonly SettingsStore _store;

    /// <summary>The single running instance, for the far-flung readers (editor/export save defaults).</summary>
    public static SettingsService? Instance { get; private set; }

    public AppSettings Current { get; private set; }

    /// <summary>Raised after settings are saved, with the new value.</summary>
    public event Action<AppSettings>? Changed;

    public SettingsService()
    {
        _store = new SettingsStore();
        Current = _store.Load();
        Instance = this;
    }

    /// <summary>Persist new settings, apply autostart, and notify listeners.</summary>
    public void Update(AppSettings settings)
    {
        Current = settings.Sanitised();
        _store.Save(Current);
        Autostart.Apply(Current.Autostart);
        Changed?.Invoke(Current);
    }

    /// <summary>Reconcile the OS with the loaded settings at startup (e.g. autostart entry).</summary>
    public void ApplyAtStartup() => Autostart.Apply(Current.Autostart);
}
