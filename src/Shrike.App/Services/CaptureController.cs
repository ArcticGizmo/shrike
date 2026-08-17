using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Shrike.App.Native;
using Shrike.App.Views;
using Shrike.Core;
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
    private bool _colorPickHandled;
    private EditorWindow? _editor;
    private CaptureMenuWindow? _menu;
    private Recorder? _recorder;
    private RecordingHudWindow? _hud;
    private RecordingRegionWindow? _regionWindow;
    private CursorSpotlightWindow? _spotlight;
    private Task<Recorder?>? _buildTask;

    // Smooth-cursor (experimental): when on for a recording, we log the pointer track live and hide the
    // real cursor so export can composite a smoothed synthetic one.
    private MouseTrackRecorder? _trackRecorder;
    private MouseHook? _mouseHook;
    private bool _smoothCursor;
    private PixelBounds _pendingRegion;

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
        // Remember the mode so the editor's quick "New capture" can repeat it. Recent just re-opens a
        // shot, and Pipette produces a colour (not an image), so neither is a "capture" to repeat.
        if (choice is not (CaptureMenuChoice.Recent or CaptureMenuChoice.Pipette))
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
            case CaptureMenuChoice.Pipette:
                BeginColorPick(frozen);
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

    /// <summary>
    /// Put a colour-pipette overlay on every monitor: the loupe magnifies the frozen snapshot and shows
    /// the live colour, and a click samples that pixel and pops the copyable HEX/RGB/HSL result panel.
    /// </summary>
    public void BeginColorPick(CapturedImage? frozen = null)
    {
        if (_overlays.Count > 0)
        {
            _overlays[0].Activate();
            return;
        }

        var monitors = MonitorsOrFallback();
        _frozen = frozen ?? TryCaptureFrozen();
        _colorPickHandled = false;

        // Esc on any overlay cancels the whole pick; a click on one emits the sampled colour.
        var session = new RegionSelectionSession();
        session.Cancelled += CloseOverlays;
        _session = session;

        foreach (var monitor in monitors)
        {
            var overlay = new OverlayWindow(session, monitor, _frozen, [], pipette: true);
            overlay.ColorPicked += OnColorPicked;
            _overlays.Add(overlay);
            overlay.Show();
        }

        _onOverlayShown?.Invoke();
    }

    private void OnColorPicked(PixelColor color)
    {
        if (_colorPickHandled)
            return; // a click on another monitor's overlay already handled this pick
        _colorPickHandled = true;

        CloseOverlays();
        var (cx, cy) = CursorPosition.Get();
        ColorResultWindow.Show(color, new PixelPoint(cx, cy));
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

    /// <summary>After a region is picked, show the region frame (with resize handles) and a single HUD bar
    /// carrying Record / Cancel. The same HUD becomes the live recording controls once capture begins, so
    /// nothing pops in after the countdown.</summary>
    private void ShowRecordingSetup(PixelBounds region)
    {
        if (_regionWindow is not null) { _regionWindow.Activate(); return; }
        if (_recorder is not null) { _hud?.Activate(); return; }

        var cursorOn = _settings?.Current.CursorInRecording ?? true;
        // The cursor is painted into frames on its own; the spotlight is a real overlay. They're
        // independent, so both can be on at once.
        var spotlightOn = _settings?.Current.SpotlightCursorEnabled ?? false;
        var style = CurrentSpotlightStyle();
        _smoothCursor = false; // experimental, opt-in per recording

        var regionWindow = new RecordingRegionWindow(region, MonitorsOrFallback());
        var hud = new RecordingHudWindow(region, spotlightOn, style, cursorOn, smoothCursorOn: false);

        // The spotlight is a real on-screen overlay (captured naturally), so it previews during setup and
        // simply carries into the recording — one source of truth for "what's being recorded".
        var spotlight = new CursorSpotlightWindow(style);
        _spotlight = spotlight;

        // Setup wiring: the HUD's Record/Cancel drive the region window; its handle drags trail the HUD.
        hud.RecordRequested += OnRecordRequested;
        hud.CancelRequested += TeardownRecordingSetup;
        hud.Finished += OnRecordingFinished;
        hud.SpotlightToggled += OnSpotlightToggled;
        hud.SpotlightStyleChanged += OnSpotlightStyleChanged;
        hud.CursorInRecordingToggled += OnCursorInRecordingToggled;
        hud.SmoothCursorToggled += OnSmoothCursorToggled;
        hud.Closed += (_, _) => { if (ReferenceEquals(_hud, hud)) _hud = null; };

        regionWindow.RegionChanged += hud.FollowRegion;
        regionWindow.Cancelled += TeardownRecordingSetup;
        regionWindow.CountdownFinished += OnCountdownFinished;
        regionWindow.Closed += (_, _) => { if (ReferenceEquals(_regionWindow, regionWindow)) _regionWindow = null; };

        _regionWindow = regionWindow;
        _hud = hud;

        regionWindow.Show();
        // Own the HUD to the region frame: Windows keeps an owned window above its owner in the z-order,
        // so dragging/raising the frame can never bury the HUD behind its scrim.
        hud.Show(regionWindow);
        hud.Activate();
        // Show the spotlight last so its glow previews above the scrim.
        spotlight.SetActive(spotlightOn);
    }

    private SpotlightStyle CurrentSpotlightStyle()
    {
        var s = _settings?.Current;
        return new SpotlightStyle(
            s?.SpotlightColor ?? "#FFD24A",
            s?.SpotlightOpacity ?? 0.30,
            s?.SpotlightRadius ?? 30);
    }

    private void OnSpotlightToggled(bool on)
    {
        _spotlight?.SetActive(on);
        if (_settings is not null && _settings.Current.SpotlightCursorEnabled != on)
            _settings.Update(_settings.Current with { SpotlightCursorEnabled = on });
    }

    private void OnCursorInRecordingToggled(bool inRecording)
    {
        if (_settings is not null && _settings.Current.CursorInRecording != inRecording)
            _settings.Update(_settings.Current with { CursorInRecording = inRecording });
    }

    /// <summary>Experimental: turning smooth-cursor on logs the pointer track and draws a smoothed cursor
    /// in post. It needs a clean plate, so it hides the real cursor and turns the live spotlight off (a
    /// baked spotlight would follow the hidden real cursor and misalign with the synthetic one).</summary>
    private void OnSmoothCursorToggled(bool on)
    {
        _smoothCursor = on;
        if (on)
            _spotlight?.SetActive(false);
        else
            _spotlight?.SetActive(_settings?.Current.SpotlightCursorEnabled ?? false);
    }

    private void OnSpotlightStyleChanged(SpotlightStyle style)
    {
        _spotlight?.UpdateStyle(style);
        if (_settings is not null)
            _settings.Update(_settings.Current with
            {
                SpotlightColor = style.Color,
                SpotlightOpacity = style.Opacity,
                SpotlightRadius = style.Radius,
            });
    }

    /// <summary>Record pressed: the region is now final, so kick off the (slow) ffmpeg pipeline build in
    /// the background while the 3-2-1 countdown runs, so capture can start the instant it hits zero.</summary>
    private void OnRecordRequested()
    {
        if (_regionWindow is null || _recorder is not null || _buildTask is not null) return;

        // Fail fast if ffmpeg is missing rather than after a 3-second countdown.
        if (Ffmpeg.Locate() is null)
        {
            ToastWindow.Show("Recording needs FFmpeg — install it (or set the SHRIKE_FFMPEG path).");
            TeardownRecordingSetup();
            return;
        }

        var region = _regionWindow.Region;
        _pendingRegion = region;
        const int fps = 30;
        // Smooth cursor needs a clean plate, so it forces the real cursor off regardless of the setting.
        var captureCursor = !_smoothCursor && (_settings?.Current.CursorInRecording ?? true);
        // A stable per-profile working folder (not %TEMP%, which the OS can purge) so the source MP4 and
        // its *.track.json sidecar survive to be edited / re-exported.
        var path = Path.Combine(AppStorage.RecordingsDirectory(),
            CaptureNaming.Expand(CaptureNaming.DefaultTemplate, DateTimeOffset.Now) + ".mp4");

        _buildTask = Task.Run(() => BuildRecorder(region, path, fps, captureCursor));
        _regionWindow.StartCountdown();
    }

    /// <summary>Countdown hit zero: finish building the pipeline, flip the frame to recording mode, and
    /// swap the HUD to its live controls.</summary>
    private async void OnCountdownFinished()
    {
        var build = _buildTask;
        _buildTask = null;
        if (build is null || _regionWindow is null || _hud is null) { TeardownRecordingSetup(); return; }

        Recorder? recorder = null;
        string? error = null;
        try { recorder = await build; }
        catch (Exception ex) { error = ex.Message; }

        if (recorder is null)
        {
            ToastWindow.Show(error is null
                ? "Recording needs FFmpeg — install it (or set the SHRIKE_FFMPEG path)."
                : $"Couldn't start recording: {error}");
            TeardownRecordingSetup();
            return;
        }

        _recorder = recorder;

        _regionWindow.EnterRecordingMode();
        _hud.BeginRecording(recorder);
        recorder.Start();
        StartTrackCapture(recorder);
    }

    /// <summary>Arm the smooth-cursor track for this recording: install the mouse hook and stamp each event
    /// with the recorder's pause-excluded clock. No-op unless smooth-cursor is on for this take.</summary>
    private void StartTrackCapture(Recorder recorder)
    {
        if (!_smoothCursor) return;
        try
        {
            // The recorded rectangle is the region origin with the source's even-trimmed size.
            var region = new PixelBounds(_pendingRegion.X, _pendingRegion.Y, recorder.Width, recorder.Height);
            var track = new MouseTrackRecorder(region, recorder.CaptureTimeMs);
            var hook = new MouseHook();
            hook.Moved += track.Move;
            hook.Clicked += track.Click;
            hook.Install();
            _trackRecorder = track;
            _mouseHook = hook;
        }
        catch
        {
            // If the hook can't install, the recording still proceeds — just without a track.
            DisposeMouseHook();
            _trackRecorder = null;
        }
    }

    /// <summary>Tear down the setup surfaces without recording (Cancel, Esc, or a failed pipeline build).</summary>
    private void TeardownRecordingSetup()
    {
        // Defer so we never close a window from inside its own event.
        Dispatcher.UIThread.Post(() =>
        {
            _buildTask = null;
            DisposeMouseHook();
            _trackRecorder = null;
            _hud?.Close();
            _hud = null;
            _regionWindow?.Close();
            _regionWindow = null;
            CloseSpotlight();
        });
    }

    // Locate ffmpeg and wire the capture→encode pipeline. Runs on a background thread so the ffmpeg
    // process spawn never blocks the UI. Returns null when ffmpeg is unavailable; throws for other setup
    // failures (surfaced as a toast). The cursor spotlight is a separate on-screen overlay captured
    // naturally, so nothing about it lives in this pipeline.
    private static Recorder? BuildRecorder(PixelBounds region, string path, int fps, bool captureCursor)
    {
        if (Ffmpeg.Locate() is not { } ffmpeg) return null;
        var source = new GdiFrameSource(region, captureCursor);
        try
        {
            var bitrate = BitrateFor(source.Width, source.Height, fps);
            var encoder = new FfmpegMp4Encoder(ffmpeg, path, source.Width, source.Height, fps, bitrate);
            return new Recorder(source, encoder, path, fps);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private void CloseSpotlight()
    {
        _spotlight?.Close();
        _spotlight = null;
    }

    /// <summary>Tear down the mouse hook and, if a track was captured for a saved recording, write it as a
    /// <c>*.track.json</c> sidecar next to the MP4. Best-effort — the recording is fine without it.</summary>
    private void WriteMouseTrackSidecar(string? savedPath)
    {
        var track = _trackRecorder;
        _trackRecorder = null;
        DisposeMouseHook();

        if (track is null || savedPath is null || !File.Exists(savedPath)) return;
        try
        {
            track.Build().Save(Path.ChangeExtension(savedPath, ".track.json"));
        }
        catch
        {
            // best effort — a missing track just means no smoothing is available for this clip
        }
    }

    private void DisposeMouseHook()
    {
        _mouseHook?.Dispose();
        _mouseHook = null;
    }

    private void OnRecordingFinished(string? savedPath)
    {
        // The HUD closes itself; drop the region frame (which doubled as the recording border) and the
        // spotlight overlay with it.
        _regionWindow?.Close();
        _regionWindow = null;
        CloseSpotlight();
        var recorder = _recorder;
        _recorder = null;

        // Finalise the smooth-cursor track (if any) as a sidecar next to the MP4 before we hand off.
        WriteMouseTrackSidecar(savedPath);

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
