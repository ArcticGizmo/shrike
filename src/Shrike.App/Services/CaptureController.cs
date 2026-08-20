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
    private Task<Recorder?>? _buildTask;

    // Every recording is a clean plate with the pointer path logged live, so the cursor (and future effects)
    // are composited in post. The "Show cursor" toggle only sets the clip's default — persisted per-clip so
    // the editor can flip it.
    private MouseTrackRecorder? _trackRecorder;
    private MouseHook? _mouseHook;
    private bool _showCursor = true;
    private PixelBounds _pendingRegion;
    private string? _pendingPath;

    // Audio capture (opt-in via the mic-check dialog): the sidecars are written next to the recording and
    // consumed by the editor/export. Off by default so no recording silently opens the mic.
    private AudioSidecarCapture? _audioCapture;
    private MicCheckWindow? _micCheck;
    private bool _micEnabled;
    private string? _micDeviceId;
    private bool _systemSound;

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

        // The "Show cursor" default is remembered across recordings (CursorInRecording now means "draw the
        // cursor in the edited video" — every recording is a clean plate regardless).
        _showCursor = _settings?.Current.CursorInRecording ?? true;
        _micEnabled = _settings?.Current.MicEnabled ?? false;
        _micDeviceId = _settings?.Current.MicDeviceId;
        _systemSound = _settings?.Current.SystemSoundEnabled ?? false;

        var regionWindow = new RecordingRegionWindow(region, MonitorsOrFallback());
        var hud = new RecordingHudWindow(region, _showCursor, new MicSetup(_micEnabled, _micDeviceId, _systemSound));

        // Setup wiring: the HUD's Record/Cancel drive the region window; its handle drags trail the HUD.
        hud.RecordRequested += OnRecordRequested;
        hud.CancelRequested += TeardownRecordingSetup;
        hud.Finished += OnRecordingFinished;
        hud.ShowCursorToggled += OnShowCursorToggled;
        hud.MicToggled += OnMicEnabledChanged;
        hud.SystemSoundToggled += OnSystemSoundChanged;
        hud.MicCheckRequested += OnMicCheckRequested;
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
    }

    private void OnShowCursorToggled(bool show)
    {
        _showCursor = show;
        // Remember the choice as the default for the next recording.
        if (_settings is not null && _settings.Current.CursorInRecording != show)
            _settings.Update(_settings.Current with { CursorInRecording = show });
    }

    /// <summary>Open the mic-check dialog: device, live meter, test-and-play-back, system-sound toggle. Each
    /// change is persisted immediately (remembered for next time), and the HUD reflects the armed state.</summary>
    private void OnMicCheckRequested()
    {
        if (_micCheck is not null) { _micCheck.Activate(); return; }
        if (_hud is null) return;

        var dialog = new MicCheckWindow(new MicSetup(_micEnabled, _micDeviceId, _systemSound));
        dialog.MicEnabledChanged += OnMicEnabledChanged;
        dialog.DeviceChanged += OnMicDeviceChanged;
        dialog.SystemSoundChanged += OnSystemSoundChanged;
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_micCheck, dialog)) _micCheck = null;
            _hud?.ReflectAudioState(_micEnabled, _systemSound);
        };

        _micCheck = dialog;
        dialog.Show(_hud); // owned by the HUD so it stays above the region frame
        dialog.Activate();
    }

    private void OnMicEnabledChanged(bool on)
    {
        _micEnabled = on;
        _hud?.ReflectAudioState(_micEnabled, _systemSound); // keep the HUD toggle + mic-check dialog in step
        _micCheck?.ReflectMicEnabled(on);
        if (_settings is not null && _settings.Current.MicEnabled != on)
            _settings.Update(_settings.Current with { MicEnabled = on });
    }

    private void OnMicDeviceChanged(string? id)
    {
        _micDeviceId = id;
        if (_settings is not null && _settings.Current.MicDeviceId != id)
            _settings.Update(_settings.Current with { MicDeviceId = id });
    }

    private void OnSystemSoundChanged(bool on)
    {
        _systemSound = on;
        _hud?.ReflectAudioState(_micEnabled, _systemSound); // keep the HUD toggle + mic-check dialog in step
        _micCheck?.ReflectSystemSound(on);
        if (_settings is not null && _settings.Current.SystemSoundEnabled != on)
            _settings.Update(_settings.Current with { SystemSoundEnabled = on });
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
        // Always record a clean plate (no baked cursor) — the pointer path is logged and the cursor is drawn
        // in post, so it (and future effects) stay fully editable.
        const bool captureCursor = false;
        // A stable per-profile working folder (not %TEMP%, which the OS can purge) so the source MP4 and
        // its *.track.json sidecar survive to be edited / re-exported.
        var path = Path.Combine(AppStorage.RecordingsDirectory(),
            CaptureNaming.Expand(CaptureNaming.DefaultTemplate, DateTimeOffset.Now) + ".mp4");
        _pendingPath = path; // used to derive the audio sidecar paths when capture starts

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
        StartAudioCapture(recorder);
    }

    /// <summary>Arm mic and/or system-sound capture for this recording if the user enabled them in the
    /// mic-check dialog. Writes WAV sidecars aligned to the recorder's pause-excluded clock; tolerant of a
    /// device that won't open (the recording proceeds without that source).</summary>
    private void StartAudioCapture(Recorder recorder)
    {
        if (_pendingPath is null || (!_micEnabled && !_systemSound)) return;
        try
        {
            _audioCapture = AudioSidecarCapture.Start(
                _pendingPath, _micEnabled, _micDeviceId, _systemSound, recorder.CaptureTimeMs);
        }
        catch
        {
            _audioCapture = null; // never let an audio failure stop the recording
        }
    }

    /// <summary>Arm the pointer track for this recording: install the mouse hook and stamp each event with the
    /// recorder's pause-excluded clock. Always on — every recording logs the path so post can draw the cursor.</summary>
    private void StartTrackCapture(Recorder recorder)
    {
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
            _pendingPath = null;
            DisposeMouseHook();
            _trackRecorder = null;
            _audioCapture?.Dispose();
            _audioCapture = null;
            _micCheck?.Close();
            _micCheck = null;
            _hud?.Close();
            _hud = null;
            _regionWindow?.Close();
            _regionWindow = null;
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

    /// <summary>Tear down the mouse hook and, for a saved recording, write the pointer track as a
    /// <c>*.track.json</c> sidecar next to the MP4, plus the clip's initial edit document carrying the
    /// "Show cursor" default. Best-effort — the recording is fine without them.</summary>
    private void WriteMouseTrackSidecar(string? savedPath)
    {
        var track = _trackRecorder;
        _trackRecorder = null;
        DisposeMouseHook();

        if (track is null || savedPath is null || !File.Exists(savedPath)) return;
        try
        {
            track.Build().Save(AppStorage.SidecarFor(savedPath));
            // Seed the clip's edit document with the capture-time cursor default (only persists if non-default).
            new ClipEdit(ZoomTrack.Empty, _showCursor).Save(AppStorage.EditDocFor(savedPath));
        }
        catch
        {
            // best effort — a missing track just means no cursor overlay is available for this clip
        }
    }

    /// <summary>Keep the recordings working folder bounded — run off the UI thread after a recording lands.
    /// The just-saved clip is the newest, so it's always kept (and may be open in the editor).</summary>
    private static void SweepRecordingsInBackground()
    {
        if (!OperatingSystem.IsWindows()) return;
        Task.Run(() =>
        {
            try { RecordingsRetention.Sweep(AppStorage.RecordingsDirectory(), RecordingRetention.Default, DateTimeOffset.UtcNow); }
            catch { /* best effort */ }
        });
    }

    private void DisposeMouseHook()
    {
        _mouseHook?.Dispose();
        _mouseHook = null;
    }

    /// <summary>Stop and finalise the audio sidecars off the UI thread (Dispose patches each WAV and closes
    /// the device, which can briefly block). For a discarded take (no saved MP4) the sidecars are orphans, so
    /// they're deleted rather than left for the retention sweep.</summary>
    private void FinalizeAudioCapture(string? savedPath)
    {
        var audio = _audioCapture;
        _audioCapture = null;
        if (audio is null) return;

        // Finalise synchronously so the WAV header (sizes) is patched before the editor opens and reads the
        // sidecar to seed its audio clip. This is a light stop/close (no ffmpeg flush, unlike the video path).
        var paths = audio.WrittenPaths.ToArray();
        try { audio.Stop(); audio.Dispose(); } catch { /* best effort */ }

        if (savedPath is null) // discarded take: no MP4, so the sidecars are orphans — remove them
            foreach (var p in paths)
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
    }

    private void OnRecordingFinished(string? savedPath)
    {
        // The HUD closes itself; drop the region frame (which doubled as the recording border) with it.
        _regionWindow?.Close();
        _regionWindow = null;
        var recorder = _recorder;
        _recorder = null;

        // Finalise the audio sidecars (patch WAV sizes, close devices) — off the UI thread; if the take was
        // discarded there's no MP4, so the orphaned sidecars are deleted.
        FinalizeAudioCapture(savedPath);

        // Finalise the smooth-cursor track (if any) as a sidecar next to the MP4 before we hand off.
        WriteMouseTrackSidecar(savedPath);

        // Reclaim old working recordings now that this one has landed (newest is always kept).
        SweepRecordingsInBackground();

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
