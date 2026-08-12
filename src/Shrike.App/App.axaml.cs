using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Shrike.App.Services;
using Shrike.Core.Interop;
using Shrike.Core.Ipc;
using Shrike.Core.Startup;

namespace Shrike.App;

public partial class App : Application
{
    private readonly VirtualDesktopService _desktops = VirtualDesktopService.Create();
    private HotkeyService? _hotkeys;
    private CaptureController? _capture;
    private TrayIcon? _tray;
    private bool _overlayMarked;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Tray-resident: there is no main window, so the app must not exit when no window is open.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _capture = new CaptureController(_desktops, onOverlayShown: MarkOverlayShownOnce);

            SetupTray(desktop);

            if (OperatingSystem.IsWindows())
            {
                _hotkeys = new HotkeyService();
                _hotkeys.CaptureRequested += () => _capture?.ShowCaptureMenu();
                _hotkeys.Start();
            }

            // Forwarded actions from a second launch arrive on a pool thread — marshal to the UI thread.
            AppEnv.SingleInstance?.StartServer(action =>
                Dispatcher.UIThread.Post(() => OnAction(action)));

            AppEnv.Budget?.Mark(StartupMarks.TrayReady);

            if (AppEnv.MeasureMode)
                RunMeasureAndExit(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://shrike/Assets/shrike-tray.png")));

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

        var recent = new NativeMenuItem("Recent (coming soon)") { IsEnabled = false };
        var settings = new NativeMenuItem("Settings (coming soon)") { IsEnabled = false };

        var quit = new NativeMenuItem("Quit Shrike");
        quit.Click += (_, _) => desktop.Shutdown();

        var menu = new NativeMenu();
        menu.Add(capture);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(region);
        menu.Add(monitor);
        menu.Add(full);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(recent);
        menu.Add(settings);
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
