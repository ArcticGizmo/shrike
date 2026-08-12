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
                _hotkeys.Triggered += OnAction;
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

        var capture = new NativeMenuItem("Capture region")
        {
            Gesture = new KeyGesture(Key.Q, KeyModifiers.Alt | KeyModifiers.Shift),
        };
        capture.Click += (_, _) => OnAction(CaptureAction.ShowOverlay);

        var recent = new NativeMenuItem("Recent (coming soon)") { IsEnabled = false };
        var settings = new NativeMenuItem("Settings (coming soon)") { IsEnabled = false };

        var quit = new NativeMenuItem("Quit Shrike");
        quit.Click += (_, _) => desktop.Shutdown();

        var menu = new NativeMenu();
        menu.Add(capture);
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
        _tray.Clicked += (_, _) => OnAction(CaptureAction.ShowOverlay);

        TrayIcon.SetIcons(this, [_tray]);
    }

    private void OnAction(CaptureAction action)
    {
        // Every capture intent currently routes to region capture; the disabled tray entries (recent,
        // settings) are placeholders the later milestones light up.
        switch (action)
        {
            case CaptureAction.ShowRecent:
            case CaptureAction.ShowSettings:
                break;
            default:
                _capture?.BeginRegionCapture();
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
