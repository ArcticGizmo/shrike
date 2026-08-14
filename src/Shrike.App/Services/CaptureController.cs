using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Shrike.App.Native;
using Shrike.App.Views;
using Shrike.Core.Capture;
using Shrike.Core.Interop;
using Shrike.Core.Recording;
using Shrike.Core.Settings;

namespace Shrike.App.Services;

/// <summary>
/// Orchestrates a capture: pop a region overlay on every monitor → grab the pixels → show them in
/// the (reused) editor. Enforces the no-desktop-switch rule when surfacing the editor.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class CaptureController
{
    private readonly VirtualDesktopService _desktops;
    private readonly RecentRing _ring;
    private readonly SettingsService? _settings;
    private readonly Action? _onOverlayShown;

    private readonly List<OverlayWindow> _overlays = [];
    private readonly List<DimWindow> _dimmers = [];
    private RegionSelectionSession? _session;
    private CapturedImage? _frozen;
    private EditorWindow? _editor;
    private CaptureMenuWindow? _menu;
    private Recorder? _recorder;
    private RecordingHudWindow? _hud;
    private RecordingBorderWindow? _border;
    private RecordingSetupWindow? _setup;

    /// <summary>The last capture mode the user ran, so the editor's "New capture" button can repeat it.</summary>
    private CaptureMenuChoice _lastCaptureChoice = CaptureMenuChoice.Region;

    public CaptureController(VirtualDesktopService desktops, RecentRing ring,
        SettingsService? settings = null, Action? onOverlayShown = null)
    {
        _desktops = desktops;
        _ring = ring;
        _settings = settings;
        _onOverlayShown = onOverlayShown;
    }

    /// <summary>
    /// Open the capture chooser at the cursor. The single hotkey and the tray both route here; the
    /// chosen mode then runs. This is the one entry point (and the seam where "Record" will slot in).
    /// </summary>
    public void ShowCaptureMenu()
    {
        if (_menu is not null)
        {
            _menu.Activate();
            return;
        }

        var monitors = MonitorsOrFallback();

        // Freeze the CLEAN desktop first — before we dim — so no scrim ever ends up in a capture.
        // Every mode works from this snapshot.
        var frozen = TryCaptureFrozen();

        // Dim every monitor behind the chooser to signal "you're mid-capture". A click on any dimmer
        // (i.e. outside the chooser) cancels.
        foreach (var monitor in monitors)
        {
            var dim = new DimWindow(monitor);
            dim.Dismissed += TeardownChooser;
            _dimmers.Add(dim);
            dim.Show();
        }

        var (cx, cy) = CursorPosition.Get();
        var menu = new CaptureMenuWindow(new PixelPoint(cx, cy), _ring.Count);
        menu.Chosen += choice =>
        {
            TeardownChooser();
            RunChoice(choice, frozen);
        };
        menu.Cancelled += TeardownChooser;
        menu.Closed += (_, _) => { if (ReferenceEquals(_menu, menu)) _menu = null; };

        _menu = menu;
        menu.Show();     // shown last, so it sits above the dimmers and takes focus
        menu.Activate();
    }

    /// <summary>Editor "New capture": repeat the last mode straight away. The editor minimises itself
    /// first (see <see cref="EditorWindow"/>), so we wait a beat for it to leave the screen before the
    /// freeze, then run the mode live (null frozen → each mode grabs a fresh snapshot).</summary>
    public void RepeatLastCapture()
        => RunAfter(EditorHideSettle, () => RunChoice(_lastCaptureChoice, null));

    /// <summary>Editor "New capture ▾": open the full chooser after the editor has minimised out of shot.</summary>
    public void ShowCaptureMenuFromEditor()
        => RunAfter(EditorHideSettle, ShowCaptureMenu);

    /// <summary>Time to let the editor's minimise settle before we freeze the screen for a new capture.</summary>
    private static readonly TimeSpan EditorHideSettle = TimeSpan.FromMilliseconds(200);

    private void RunChoice(CaptureMenuChoice choice, CapturedImage? frozen)
    {
        // Remember the mode so the editor's quick "New capture" can repeat it (Recent isn't a capture).
        if (choice is not CaptureMenuChoice.Recent)
            _lastCaptureChoice = choice;

        switch (choice)
        {
            case CaptureMenuChoice.Region:
                BeginRegionCapture(frozen);
                break;
            case CaptureMenuChoice.Monitor:
                CaptureFromFrozen(frozen, MonitorUnderCursorBounds());
                break;
            case CaptureMenuChoice.AllMonitors:
                CaptureFromFrozen(frozen, ScreenCapture.VirtualScreenBounds());
                break;
            case CaptureMenuChoice.Record:
                BeginRegionRecording(frozen);
                break;
            case CaptureMenuChoice.Recent:
                // Open the editor on the newest capture; the filmstrip surfaces the rest.
                if (_ring.Items.Count > 0)
                    OpenInEditor(_ring.Items[0].Image);
                break;
        }
    }

    private void TeardownChooser()
    {
        // Defer so we never close a window from inside its own pointer/key event.
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var dim in _dimmers.ToArray())
                dim.Close();
            _dimmers.Clear();

            _menu?.Close();
            _menu = null;
        });
    }

    /// <summary>Show a region-selection overlay on each monitor, then take a screenshot of the pick.</summary>
    public void BeginRegionCapture(CapturedImage? frozen = null)
        => StartRegionSelection(frozen, OnRegionSelected);

    /// <summary>Show a region-selection overlay on each monitor, then open the recording setup step.</summary>
    public void BeginRegionRecording(CapturedImage? frozen = null)
        => StartRegionSelection(frozen, ShowRecordingSetup);

    /// <summary>
    /// Put a region-selection overlay on every monitor (or focus the existing set) and route the chosen
    /// rectangle to <paramref name="onCompleted"/> — shared by screenshot and recording region picks.
    /// </summary>
    private void StartRegionSelection(CapturedImage? frozen, Action<PixelBounds> onCompleted)
    {
        if (_overlays.Count > 0)
        {
            _overlays[0].Activate();
            return;
        }

        var monitors = MonitorsOrFallback();

        // Reuse the chooser's clean snapshot when there is one; otherwise freeze now. The magnifier
        // samples this and the final selection is cropped from it (WYSIWYG with the loupe).
        _frozen = frozen ?? TryCaptureFrozen();

        // Snapshot window rectangles BEFORE showing the overlays, so our overlays aren't in the list.
        var windows = TopLevelWindows.Enumerate();

        var session = new RegionSelectionSession();
        session.Completed += region =>
        {
            CloseOverlays();
            onCompleted(region);
        };
        session.Cancelled += CloseOverlays;
        _session = session;

        foreach (var monitor in monitors)
        {
            var overlay = new OverlayWindow(session, monitor, _frozen, windows);
            _overlays.Add(overlay);
            overlay.Show();
        }

        _onOverlayShown?.Invoke();
    }

    private static CapturedImage? TryCaptureFrozen()
    {
        try
        {
            return ScreenCapture.Capture(ScreenCapture.VirtualScreenBounds());
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<MonitorInfo> MonitorsOrFallback()
    {
        var monitors = Monitors.All();
        return monitors.Count > 0
            ? monitors
            : [new MonitorInfo(ScreenCapture.VirtualScreenBounds(), 1.0, true)];
    }

    private static PixelBounds MonitorUnderCursorBounds()
    {
        var (cx, cy) = CursorPosition.Get();
        var monitor = Monitors.All().FirstOrDefault(m =>
            cx >= m.Bounds.X && cx < m.Bounds.Right && cy >= m.Bounds.Y && cy < m.Bounds.Bottom);
        return monitor.Bounds.IsEmpty ? ScreenCapture.VirtualScreenBounds() : monitor.Bounds;
    }

    /// <summary>Crop the given clean snapshot to <paramref name="bounds"/>, or grab live if there's none.</summary>
    private void CaptureFromFrozen(CapturedImage? frozen, PixelBounds bounds)
    {
        if (bounds.IsEmpty)
            return;

        if (frozen is not null)
        {
            try
            {
                ShowInEditor(frozen.Crop(bounds));
                return;
            }
            catch
            {
                // bounds fell outside the snapshot — fall back to a live grab
            }
        }

        CaptureAndEdit(bounds);
    }

    /// <summary>Dismiss the overlays if open (used by the measure-startup path).</summary>
    public void CancelOverlay() => _session?.Cancel();

    /// <summary>Capture the whole (all-monitor) desktop straight to the editor.</summary>
    public void CaptureFullScreen() => CaptureAndEdit(ScreenCapture.VirtualScreenBounds());

    /// <summary>Capture the monitor the pointer is currently on (live — for the CLI/IPC path).</summary>
    public void CaptureMonitorUnderCursor() => CaptureAndEdit(MonitorUnderCursorBounds());

    /// <summary>Run a capture action after a delay (for menus/hover states). Zero delay runs now.</summary>
    public void RunAfter(TimeSpan delay, Action action)
    {
        if (delay <= TimeSpan.Zero)
        {
            action();
            return;
        }

        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    private void CaptureAndEdit(PixelBounds bounds)
    {
        if (bounds.IsEmpty)
            return;

        CapturedImage image;
        try
        {
            image = ScreenCapture.Capture(bounds);
        }
        catch
        {
            return;
        }

        ShowInEditor(image);
    }

    private void CloseOverlays()
    {
        // Defer so we never close a window from inside its own pointer/key event.
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var overlay in _overlays.ToArray())
                overlay.Close();
            _overlays.Clear();
            _session = null;
            _frozen = null;
        });
    }

    private void OnRegionSelected(PixelBounds region)
    {
        // Prefer cropping the frozen snapshot (WYSIWYG with the loupe); fall back to a fresh grab.
        if (_frozen is not null)
        {
            try
            {
                ShowInEditor(_frozen.Crop(region));
                return;
            }
            catch
            {
                // region outside the frozen buffer — fall through to a live capture
            }
        }

        CaptureAndEdit(region);
    }

    // ---- recording (M4.3) ----

    /// <summary>After a region is picked, let the user fine-tune it with handles and press Record (with a
    /// 3-2-1 countdown) before anything is captured. Confirmed → the actual recording starts.</summary>
    private void ShowRecordingSetup(PixelBounds region)
    {
        if (_setup is not null) { _setup.Activate(); return; }
        if (_recorder is not null) { _hud?.Activate(); return; }

        var setup = new RecordingSetupWindow(region, MonitorsOrFallback());
        setup.Confirmed += final => StartRecording(final);
        setup.Closed += (_, _) => { if (ReferenceEquals(_setup, setup)) _setup = null; };
        _setup = setup;
        setup.Show();
        setup.Activate();
    }

    /// <summary>Begin recording the chosen region: show the frame immediately, build the pipeline off the
    /// UI thread (locating ffmpeg and spawning the encoder would otherwise stutter the UI), then reveal
    /// the HUD once it's ready.</summary>
    private async void StartRecording(PixelBounds region)
    {
        if (_recorder is not null)   // one recording at a time
        {
            _hud?.Activate();
            return;
        }

        // The boundary appears at once, so the user sees what's captured without waiting on ffmpeg.
        var border = new RecordingBorderWindow(region);
        _border = border;
        border.Show();

        const int fps = 30;
        var path = Path.Combine(Path.GetTempPath(),
            CaptureNaming.Expand(CaptureNaming.DefaultTemplate, DateTimeOffset.Now) + ".mp4");

        var enhanceMouse = _settings?.Current.EnhanceMouseInRecording ?? false;

        (Recorder Recorder, CursorGlowFrameSource Glow)? built = null;
        string? error = null;
        try { built = await Task.Run(() => BuildRecorder(region, path, fps, enhanceMouse)); }
        catch (Exception ex) { error = ex.Message; }

        if (built is not { } pipeline)
        {
            CloseBorder();
            ToastWindow.Show(error is null
                ? "Recording needs FFmpeg — install it (or set the SHRIKE_FFMPEG path)."
                : $"Couldn't start recording: {error}");
            return;
        }

        var recorder = pipeline.Recorder;
        _recorder = recorder;

        var hud = new RecordingHudWindow(recorder, region, pipeline.Glow, enhanceMouse, OnEnhanceMouseChanged);
        hud.Finished += OnRecordingFinished;
        hud.Closed += (_, _) => { if (ReferenceEquals(_hud, hud)) _hud = null; };
        _hud = hud;

        recorder.Start();
        hud.Show();
        hud.Activate();
    }

    // Locate ffmpeg and wire the capture→encode pipeline. Runs on a background thread so the ffmpeg
    // process spawn never blocks the UI. Returns null when ffmpeg is unavailable; throws for other
    // setup failures (surfaced as a toast). The GDI grab is wrapped in a cursor-glow decorator the HUD
    // can toggle live.
    private static (Recorder, CursorGlowFrameSource)? BuildRecorder(PixelBounds region, string path, int fps, bool enhanceMouse)
    {
        if (Ffmpeg.Locate() is not { } ffmpeg) return null;
        var gdi = new GdiFrameSource(region);
        var glow = new CursorGlowFrameSource(gdi, region, enhanceMouse);
        try
        {
            var bitrate = BitrateFor(glow.Width, glow.Height, fps);
            var encoder = new FfmpegMp4Encoder(ffmpeg, path, glow.Width, glow.Height, fps, bitrate);
            return (new Recorder(glow, encoder, path, fps), glow);
        }
        catch
        {
            glow.Dispose();
            throw;
        }
    }

    /// <summary>Remember the HUD's "enhance mouse" choice so the next recording defaults to it.</summary>
    private void OnEnhanceMouseChanged(bool enabled)
    {
        if (_settings is null || _settings.Current.EnhanceMouseInRecording == enabled) return;
        _settings.Update(_settings.Current with { EnhanceMouseInRecording = enabled });
    }

    private void CloseBorder()
    {
        _border?.Close();
        _border = null;
    }

    private void OnRecordingFinished(string? savedPath)
    {
        CloseBorder();
        var recorder = _recorder;
        _recorder = null;
        if (savedPath is null || !File.Exists(savedPath)) return;

        // Hand the source to the timeline editor to trim and export (M5). Too-short clips (or a missing
        // ffmpeg) skip straight to revealing the file — the M4 drag-into-Slack path.
        if (recorder is not null && recorder.Duration >= TimeSpan.FromMilliseconds(500) && Ffmpeg.Locate() is { } ffmpeg)
        {
            var source = new RecordingSource(savedPath, recorder.Width, recorder.Height, recorder.Fps, recorder.Duration);
            var editor = new TimelineEditorWindow(source, ffmpeg);
            editor.Show();
            editor.Activate();
            return;
        }

        RevealInExplorer(savedPath);
    }

    private static void RevealInExplorer(string path)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    // ~0.1 bits per pixel per frame, clamped to a sane band, as the default target bitrate.
    private static int BitrateFor(int width, int height, int fps)
        => (int)Math.Clamp((long)width * height * fps / 10, 1_000_000, 12_000_000);

    /// <summary>Re-open a capture already in the recent ring, without pushing a duplicate entry.</summary>
    public void OpenInEditor(CapturedImage image) => ShowInEditor(image, addToRing: false);

    private void ShowInEditor(CapturedImage image, bool addToRing = true)
    {
        if (addToRing)
            _ring.Add(image);

        // "New window here" opens a fresh editor each time on the current desktop; "follow me" (default)
        // reuses the one pre-built editor and brings it to the desktop you're looking at.
        var newWindowHere = _settings?.Current.DesktopBehaviour == DesktopBehaviour.NewWindowHere;

        var editor = _editor;
        if (editor is null || newWindowHere)
        {
            editor = new EditorWindow();
            editor.Closed += (_, _) => { if (ReferenceEquals(_editor, editor)) _editor = null; };
            _editor = editor;
        }

        // Wire the strip to the ring + re-open path (idempotent; the editor guards re-subscribe).
        editor.AttachRecentRing(_ring, OpenInEditor);
        editor.ConfigureNewCapture(RepeatLastCapture, ShowCaptureMenuFromEditor);
        editor.SetCapture(image);

        // A capture started from the editor minimises it out of shot; bring it back for the result.
        if (editor.WindowState == WindowState.Minimized)
            editor.WindowState = WindowState.Normal;

        // No desktop teleport: if the reused editor is parked on another desktop, bring it to the one
        // the user is looking at (the foreground window's desktop) rather than switching them there.
        if (editor.IsVisible)
        {
            var hwnd = editor.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (_desktops.IsWindowOnCurrentDesktop(hwnd) == false)
                _desktops.TryMoveToDesktopOf(hwnd, ForegroundWindow.Get());
        }

        editor.Show();
        editor.Activate();
    }
}
