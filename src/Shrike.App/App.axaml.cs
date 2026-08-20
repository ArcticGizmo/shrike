using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Shrike.App.Imaging;
using Shrike.App.Native;
using Shrike.App.Services;
using Shrike.App.Updates;
using Shrike.App.Views;
using Shrike.Core;
using Shrike.Core.Capture;
using Shrike.Core.Changelog;
using Shrike.Core.Interop;
using Shrike.Core.Ipc;
using Shrike.Core.Settings;
using Shrike.Core.Startup;

namespace Shrike.App;

public partial class App : Application
{
    private readonly VirtualDesktopService _desktops = VirtualDesktopService.Create();
    private RecentRing _recentRing = new();
    private SettingsService? _settings;
    private HotkeyService? _hotkeys;
    private CaptureController? _capture;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private ChangelogWindow? _changelogWindow;
    private IReadOnlyList<ChangelogSection>? _pendingChangelog;
    private bool _overlayMarked;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Tray-resident: there is no main window, so the app must not exit when no window is open.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Settings first — the ring size and hotkeys below come from them.
            if (OperatingSystem.IsWindows())
            {
                _settings = new SettingsService();
                _settings.ApplyAtStartup();
                var s = _settings.Current;
                _recentRing = new RecentRing(s.RingSize, s.RingByteCap);

                // Work out the "what's new" entries against the last-seen version, then record that this
                // version has now run (so the popup shows once per update).
                _pendingChangelog = ResolvePendingChangelog(s);
                if (s.LastSeenVersion != AppVersion.Current)
                    _settings.Update(s with { LastSeenVersion = AppVersion.Current });
            }

            _capture = new CaptureController(_desktops, _recentRing, _settings, MarkOverlayShownOnce);

            SetupTray(desktop);

            if (OperatingSystem.IsWindows())
            {
                _hotkeys = new HotkeyService();
                _hotkeys.CaptureRequested += () => _capture?.ShowCaptureMenu();
                _hotkeys.Start();
                _hotkeys.Apply(_settings!.Current.CaptureHotkey);
                // Re-register whenever the user rebinds in the settings window.
                _settings.Changed += ns => _hotkeys?.Apply(ns.CaptureHotkey);
            }

            // Forwarded actions from a second launch arrive on a pool thread — marshal to the UI thread.
            AppEnv.SingleInstance?.StartServer(action =>
                Dispatcher.UIThread.Post(() => OnAction(action)));

            // Pre-warm the ffmpeg lookup off the UI thread so the first recording doesn't pay the probe
            // cost (spawning ffmpeg -version across candidates) right when the user hits Record.
            if (OperatingSystem.IsWindows())
                Task.Run(() => { try { Shrike.Core.Recording.Ffmpeg.Locate(); } catch { /* best effort */ } });

            // Reclaim old working recordings left over from past sessions (bounded folder, off the UI thread).
            if (OperatingSystem.IsWindows())
                Task.Run(() =>
                {
                    try
                    {
                        Shrike.Core.Recording.RecordingsRetention.Sweep(
                            AppStorage.RecordingsDirectory(),
                            Shrike.Core.Recording.RecordingRetention.Default,
                            DateTimeOffset.UtcNow);
                    }
                    catch { /* best effort */ }
                });

            // Notify-only update check in the background (no-op on dev builds). A quiet toast if newer.
            CheckForUpdatesInBackground();

            // If this is the first launch after an update, show what changed.
            ShowPendingChangelogIfAny();

            AppEnv.Budget?.Mark(StartupMarks.TrayReady);

            if (AppEnv.MeasureMode)
                RunMeasureAndExit(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://shrike/Assets/tray.png")));

        // Read-only version header (mirrors perch). The (Dev) suffix marks an isolated dev instance.
        var versionItem = new NativeMenuItem($"Shrike{AppProfile.DisplaySuffix} - {AppVersion.Current}")
        {
            IsEnabled = false,
        };

        var settings = new NativeMenuItem("Settings…");
        settings.Click += (_, _) => OpenSettings();

        // Sits below Settings; opens Settings (which now hosts About + updates) and runs a check straight away.
        var checkUpdates = new NativeMenuItem("Check for updates…");
        checkUpdates.Click += (_, _) => OpenSettingsAndCheckForUpdates();

        var quit = new NativeMenuItem("Quit Shrike");
        quit.Click += (_, _) => desktop.Shutdown();

        var menu = new NativeMenu();
        menu.Add(versionItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(settings);
        menu.Add(checkUpdates);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);

        _tray = new TrayIcon
        {
            Icon = icon,
            ToolTipText = $"Shrike{AppProfile.DisplaySuffix} — ready",
            Menu = menu,
            IsVisible = true,
        };
        // Left-click stays the fast path to a capture: it opens the chooser (hotkey Alt+Shift+Q too).
        _tray.Clicked += (_, _) => _capture?.ShowCaptureMenu();

        TrayIcon.SetIcons(this, [_tray]);
    }

    private void OnAction(CaptureAction action)
    {
        // Routing for actions forwarded from a second launch (CLI verbs). The hotkey and tray call the
        // controller directly. Recent/Settings are placeholders the later milestones light up.
        switch (action)
        {
            case CaptureAction.CaptureFullScreen:
                _capture?.CaptureFullScreen();
                break;
            case CaptureAction.CaptureMonitor:
                _capture?.CaptureMonitorUnderCursor();
                break;
            case CaptureAction.CaptureRegion:
                _capture?.BeginRegionCapture();
                break;
            case CaptureAction.ShowRecent:
            case CaptureAction.ShowSettings:
                break;
            default: // ShowOverlay / CaptureWindow → the chooser
                _capture?.ShowCaptureMenu();
                break;
        }
    }

    private SettingsWindow? OpenSettings()
    {
        if (!OperatingSystem.IsWindows() || _settings is null) return null;
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return _settingsWindow; }

        var win = new SettingsWindow(_settings);
        win.Closed += (_, _) => { if (ReferenceEquals(_settingsWindow, win)) _settingsWindow = null; };
        _settingsWindow = win;
        win.Show();
        win.Activate();
        return win;
    }

    /// <summary>Tray "Check for updates…": open Settings (now home to About + updates) and kick off a check
    /// so the result lands where the user can also install it.</summary>
    private void OpenSettingsAndCheckForUpdates() => OpenSettings()?.CheckForUpdates();

    private static void CheckForUpdatesInBackground()
    {
        if (!OperatingSystem.IsWindows()) return;
        _ = Task.Run(async () =>
        {
            string? notice;
            try { notice = await UpdateChecker.CheckAsync(); }
            catch { return; }   // never let a flaky feed disturb a normal launch
            if (notice is not null)
                Dispatcher.UIThread.Post(() => ToastWindow.Show(notice));
        });
    }

    // The changelog sections released since the version that last ran here — or null when there's nothing
    // to show (suppressed, a fresh install with no history, same version, or no embedded changelog).
    private static IReadOnlyList<ChangelogSection>? ResolvePendingChangelog(AppSettings settings)
    {
        if (!settings.ShowChangelogOnUpdate) return null;
        if (string.IsNullOrWhiteSpace(settings.LastSeenVersion)) return null;   // fresh install — no history
        if (settings.LastSeenVersion == AppVersion.Current) return null;        // same version — no update
        var markdown = ChangelogView.LoadEmbedded();
        if (markdown is null) return null;
        var sections = ChangelogParser.UnseenSince(markdown, settings.LastSeenVersion, AppVersion.Current);
        return sections.Count > 0 ? sections : null;
    }

    private void ShowPendingChangelogIfAny()
    {
        if (_pendingChangelog is not { Count: > 0 } sections) return;
        // Post at background priority so the tray is up first, matching the update-check toast.
        Dispatcher.UIThread.Post(() =>
        {
            var win = new ChangelogWindow("What's new in Shrike", $"Updated to v{AppVersion.Current}", sections,
                onSuppress: () => { if (_settings is { } s) s.Update(s.Current with { ShowChangelogOnUpdate = false }); });
            win.Closed += (_, _) => { if (ReferenceEquals(_changelogWindow, win)) _changelogWindow = null; };
            _changelogWindow = win;
            win.Show();
            win.Activate();
        }, DispatcherPriority.Background);
    }

    private void MarkOverlayShownOnce()
    {
        // Record only the first overlay for the startup baseline (later shows are already warm).
        if (_overlayMarked) return;
        AppEnv.Budget?.Mark(StartupMarks.OverlayShown);
        _overlayMarked = true;
    }

    /// <summary>
    /// <c>measure-startup</c> path: show the overlay once to time the warm path, print the marks as
    /// JSON, then shut the throwaway instance down. Needs a desktop session (it renders a window);
    /// the CI budget gate itself is the headless <c>BudgetEvaluator</c> unit test.
    /// </summary>
    private void RunMeasureAndExit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _capture?.BeginRegionCapture();
            _capture?.CancelOverlay();

            Console.WriteLine(AppEnv.Budget?.ToJson() ?? "{}");
            desktop.Shutdown();
        }, DispatcherPriority.Background);
    }
}
