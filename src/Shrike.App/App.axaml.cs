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
using Shrike.Core.Capture;
using Shrike.Core.Interop;
using Shrike.Core.Ipc;
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
    private NativeMenuItem? _recentMenu;
    private SettingsWindow? _settingsWindow;
    private AboutWindow? _aboutWindow;
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
            }

            _capture = new CaptureController(_desktops, _recentRing, _settings, MarkOverlayShownOnce);

            SetupTray(desktop);

            if (OperatingSystem.IsWindows())
            {
                _hotkeys = new HotkeyService();
                _hotkeys.CaptureRequested += () => _capture?.ShowCaptureMenu();
                _hotkeys.RecordRequested += () => _capture?.BeginRegionRecording();
                _hotkeys.Start();
                _hotkeys.Apply(_settings!.Current.CaptureHotkey, _settings.Current.RecordHotkey);
                // Re-register whenever the user rebinds in the settings window.
                _settings.Changed += ns => _hotkeys?.Apply(ns.CaptureHotkey, ns.RecordHotkey);
            }

            // Forwarded actions from a second launch arrive on a pool thread — marshal to the UI thread.
            AppEnv.SingleInstance?.StartServer(action =>
                Dispatcher.UIThread.Post(() => OnAction(action)));

            // Pre-warm the ffmpeg lookup off the UI thread so the first recording doesn't pay the probe
            // cost (spawning ffmpeg -version across candidates) right when the user hits Record.
            if (OperatingSystem.IsWindows())
                Task.Run(() => { try { Shrike.Core.Recording.Ffmpeg.Locate(); } catch { /* best effort */ } });

            // Notify-only update check in the background (no-op on dev builds). A quiet toast if newer.
            CheckForUpdatesInBackground();

            AppEnv.Budget?.Mark(StartupMarks.TrayReady);

            if (AppEnv.MeasureMode)
                RunMeasureAndExit(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://shrike/Assets/icon.png")));

        // One capture entry point (the chooser), plus the individual modes for discoverability.
        var capture = new NativeMenuItem("Capture…")
        {
            Gesture = new KeyGesture(Key.Q, KeyModifiers.Alt | KeyModifiers.Shift),
        };
        capture.Click += (_, _) => _capture?.ShowCaptureMenu();

        var region = new NativeMenuItem("Region or window");
        region.Click += (_, _) => _capture?.BeginRegionCapture();
        var monitor = new NativeMenuItem("This monitor");
        monitor.Click += (_, _) => _capture?.CaptureMonitorUnderCursor();
        var full = new NativeMenuItem("All monitors");
        full.Click += (_, _) => _capture?.CaptureFullScreen();
        var record = new NativeMenuItem("Record region");
        record.Click += (_, _) => _capture?.BeginRegionRecording();

        var recent = new NativeMenuItem("Recent") { Menu = new NativeMenu() };
        _recentMenu = recent;
        _recentRing.Changed += () => Dispatcher.UIThread.Post(RebuildRecentMenu);
        RebuildRecentMenu();

        var settings = new NativeMenuItem("Settings…");
        settings.Click += (_, _) => OpenSettings();

        var about = new NativeMenuItem("About Shrike…");
        about.Click += (_, _) => OpenAbout();

        var quit = new NativeMenuItem("Quit Shrike");
        quit.Click += (_, _) => desktop.Shutdown();

        var menu = new NativeMenu();
        menu.Add(capture);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(region);
        menu.Add(monitor);
        menu.Add(full);
        menu.Add(record);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(recent);
        menu.Add(settings);
        menu.Add(about);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);

        _tray = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "Shrike — ready",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => _capture?.ShowCaptureMenu();

        TrayIcon.SetIcons(this, [_tray]);
    }

    /// <summary>
    /// Rebuild the tray "Recent" submenu from the ring (newest first). Each entry gets a thumbnail icon
    /// and a submenu of per-item actions: copy again, open in editor, delete. A trailing "Clear recent"
    /// empties the ring. When empty, a single disabled placeholder is shown.
    /// </summary>
    private void RebuildRecentMenu()
    {
        if (_recentMenu?.Menu is not { } menu)
            return;

        menu.Items.Clear();

        if (_recentRing.Count == 0)
        {
            menu.Add(new NativeMenuItem("No recent captures") { IsEnabled = false });
            _recentMenu.Header = "Recent";
            return;
        }

        _recentMenu.Header = $"Recent ({_recentRing.Count})";

        foreach (var item in _recentRing.Items)
        {
            var label = $"{item.CapturedAt.LocalDateTime:HH:mm:ss}  ·  {item.Image.Width}×{item.Image.Height}";
            var entry = new NativeMenuItem(label)
            {
                Icon = BitmapConverter.ToBitmap(item.Thumbnail),
                Menu = new NativeMenu(),
            };

            var captured = item; // capture for the closures

            var copy = new NativeMenuItem("Copy");
            copy.Click += (_, _) => CopyToClipboard(captured.Image);
            var open = new NativeMenuItem("Open in editor");
            open.Click += (_, _) => _capture?.OpenInEditor(captured.Image);
            var delete = new NativeMenuItem("Delete");
            delete.Click += (_, _) => _recentRing.Remove(captured);

            entry.Menu.Add(copy);
            entry.Menu.Add(open);
            entry.Menu.Add(delete);
            menu.Add(entry);
        }

        menu.Add(new NativeMenuItemSeparator());
        var clear = new NativeMenuItem("Clear recent");
        clear.Click += (_, _) => _recentRing.Clear();
        menu.Add(clear);
    }

    private void CopyToClipboard(CapturedImage image)
    {
        if (!OperatingSystem.IsWindows())
            return;
        // No owning window for the tray path — OpenClipboard(NULL) is valid.
        CaptureClipboard.Copy(IntPtr.Zero, image);
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

    private void OpenSettings()
    {
        if (!OperatingSystem.IsWindows() || _settings is null) return;
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }

        var win = new SettingsWindow(_settings);
        win.Closed += (_, _) => { if (ReferenceEquals(_settingsWindow, win)) _settingsWindow = null; };
        _settingsWindow = win;
        win.Show();
        win.Activate();
    }

    private void OpenAbout()
    {
        if (_aboutWindow is not null) { _aboutWindow.Activate(); return; }

        var win = new AboutWindow();
        win.Closed += (_, _) => { if (ReferenceEquals(_aboutWindow, win)) _aboutWindow = null; };
        _aboutWindow = win;
        win.Show();
        win.Activate();
    }

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
