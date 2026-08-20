using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shrike.App.Native;
using Shrike.Core.Capture;
using Shrike.Core.Recording;

namespace Shrike.App.Views;

/// <summary>
/// The single floating control bar for a recording, from setup to stop. It is born in the <b>setup</b>
/// state — a Record / Cancel pair beside the region the user is still adjusting — and, when recording
/// actually begins, swaps its contents in place to the <b>recording</b> state (live clock, pause / stop /
/// discard). One window across the whole flow, so nothing pops in after the countdown. The spotlight
/// toggle and its settings flyout live in an always-visible segment, so the cursor spotlight can be armed
/// and tuned before recording as well as during it. It's draggable, sits just outside the recording
/// region, and is excluded from capture so it never lands in its own recording.
/// </summary>
public partial class RecordingHudWindow : Window
{
    private enum HudState { Setup, Recording }

    private readonly DispatcherTimer _tick;

    private PixelBounds _region;
    private Recorder? _recorder;
    private HudState _state = HudState.Setup;
    private bool _userMoved;   // once the user drags the HUD, we stop auto-following the region
    private bool _closing;

    private StackPanel? _setupPanel;
    private StackPanel? _recordingPanel;
    private ToggleButton? _showCursorButton;
    private ToggleButton? _micToggle;
    private ToggleButton? _systemToggle;
    private TextBlock? _elapsed;
    private Button? _pauseButton;
    private Ellipse? _recDot;

    /// <summary>Raised when the user presses Record in the setup state (region is final).</summary>
    public event Action? RecordRequested;

    /// <summary>Raised when the user backs out before recording starts.</summary>
    public event Action? CancelRequested;

    /// <summary>Raised once when recording ends: the saved MP4 path, or null if discarded.</summary>
    public event Action<string?>? Finished;

    /// <summary>Raised when the "show cursor" toggle flips. The argument is whether the synthetic cursor
    /// should be drawn in the edited video (the clip's default; the editor can change it later).</summary>
    public event Action<bool>? ShowCursorToggled;

    /// <summary>Raised when the user opens the mic-check dialog (device, level meter, test, system sound).</summary>
    public event Action? MicCheckRequested;

    /// <summary>Raised when the mic on/off toggle flips (arg = whether the mic is now armed).</summary>
    public event Action<bool>? MicToggled;

    /// <summary>Raised when the system-sound toggle flips (arg = whether loopback is now armed).</summary>
    public event Action<bool>? SystemSoundToggled;

    // Parameterless ctor for the XAML designer only.
    public RecordingHudWindow() : this(default, true, default) { }

    internal RecordingHudWindow(PixelBounds region, bool showCursor, MicSetup mic)
    {
        _region = region;
        InitializeComponent();

        _setupPanel = this.FindControl<StackPanel>("SetupPanel");
        _recordingPanel = this.FindControl<StackPanel>("RecordingPanel");
        _showCursorButton = this.FindControl<ToggleButton>("ShowCursorButton");
        _micToggle = this.FindControl<ToggleButton>("MicToggle");
        _systemToggle = this.FindControl<ToggleButton>("SystemToggle");
        _elapsed = this.FindControl<TextBlock>("Elapsed");
        _pauseButton = this.FindControl<Button>("PauseButton");
        _recDot = this.FindControl<Ellipse>("RecDot");

        if (_showCursorButton is not null) _showCursorButton.IsChecked = showCursor;
        ReflectAudioState(mic.MicEnabled, mic.SystemSound);

        // SizeToContent means the real size isn't known until layout runs; re-place when it settles (and
        // again whenever the bar's width changes, e.g. swapping to the recording controls) unless the user
        // has since dragged it somewhere themselves.
        LayoutUpdated += (_, _) => AutoPlace();

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => Refresh();
    }

    private (double W, double H) _placedForSize = (-1, -1);

    private void AutoPlace()
    {
        if (_userMoved || Bounds.Width <= 0) return;
        var size = (Bounds.Width, Bounds.Height);
        if (size == _placedForSize) return;
        _placedForSize = size;
        PositionOutsideRegion();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PositionOutsideRegion();

        // Keep the HUD out of the recording (essential for full-screen captures, where there's nowhere
        // outside the region to sit). The window stays visible to the user regardless.
        if (OperatingSystem.IsWindows())
            WindowExclusion.Hide(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
    }

    /// <summary>Setup state: the region changed under the user's handle drag — trail the HUD along unless
    /// they've since dragged the HUD somewhere themselves.</summary>
    public void FollowRegion(PixelBounds region)
    {
        _region = region;
        if (_state == HudState.Setup && !_userMoved)
            PositionOutsideRegion();
    }

    /// <summary>Swap to the recording layout and start the live clock. Called once, when capture begins.</summary>
    internal void BeginRecording(Recorder recorder)
    {
        _recorder = recorder;
        _state = HudState.Recording;

        if (_setupPanel is not null) _setupPanel.IsVisible = false;
        if (_recordingPanel is not null) _recordingPanel.IsVisible = true;

        if (!_userMoved) PositionOutsideRegion();
        _tick.Start();
        Refresh();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // Drag the HUD from anywhere except a button — the clock/grip area moves the window.
        if (e.Source is Visual v && v.GetSelfAndVisualAncestors().Any(a => a is Button or ToggleButton or Slider))
            return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _userMoved = true;
            BeginMoveDrag(e);
        }
    }

    // ---- cursor control ----

    private void OnToggleShowCursor(object? sender, RoutedEventArgs e)
        => ShowCursorToggled?.Invoke(_showCursorButton?.IsChecked ?? true);

    // ---- audio arming ----

    private void OnOpenMicCheck(object? sender, RoutedEventArgs e) => MicCheckRequested?.Invoke();

    private void OnToggleMic(object? sender, RoutedEventArgs e) => MicToggled?.Invoke(_micToggle?.IsChecked ?? false);

    private void OnToggleSystem(object? sender, RoutedEventArgs e) => SystemSoundToggled?.Invoke(_systemToggle?.IsChecked ?? false);

    /// <summary>Set the mic + system-sound toggles to reflect the current arming (e.g. after the mic-check dialog
    /// changes them). Programmatic — setting <c>IsChecked</c> doesn't re-raise <c>Click</c>, so there's no loop.
    /// A checked toggle already shows the accent fill via the <c>:checked</c> style.</summary>
    internal void ReflectAudioState(bool micEnabled, bool systemSound)
    {
        if (_micToggle is not null) _micToggle.IsChecked = micEnabled;
        if (_systemToggle is not null) _systemToggle.IsChecked = systemSound;
    }

    // ---- setup / recording actions ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // The region frame is non-activating, so the HUD owns the setup shortcuts.
        if (_state != HudState.Setup) return;
        if (e.Key == Key.Escape) { e.Handled = true; CancelRequested?.Invoke(); }
        else if (e.Key is Key.Enter or Key.Return) { e.Handled = true; TryStartRecord(); }
    }

    private void OnRecord(object? sender, RoutedEventArgs e) => TryStartRecord();

    private void TryStartRecord()
    {
        if (_state != HudState.Setup || _setupPanel is { IsEnabled: false }) return;
        // Lock the setup controls; the region is now final and the countdown owns the screen.
        if (_setupPanel is not null) _setupPanel.IsEnabled = false;
        RecordRequested?.Invoke();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (_state != HudState.Setup) return;
        CancelRequested?.Invoke();
    }

    private void Refresh()
    {
        if (_recorder is null) return;

        var t = _recorder.Elapsed;
        if (_elapsed is not null)
            _elapsed.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

        var paused = _recorder.State == RecordingState.Paused;
        if (_pauseButton is not null)
            _pauseButton.Content = paused ? "Resume" : "Pause";
        // Blink the dot only while actively recording; steady/dim while paused.
        if (_recDot is not null)
            _recDot.Opacity = paused ? 0.35 : (DateTime.Now.Millisecond < 500 ? 1.0 : 0.4);
    }

    private void OnPauseResume(object? sender, RoutedEventArgs e)
    {
        if (_recorder is null) return;
        if (_recorder.State == RecordingState.Paused) _recorder.Resume();
        else if (_recorder.State == RecordingState.Recording) _recorder.Pause();
        Refresh();
    }

    private async void OnStop(object? sender, RoutedEventArgs e)
    {
        if (_closing || _recorder is null) return;
        _closing = true;
        _tick.Stop();
        SetBusy("Saving…");

        // Finalising joins the capture loop and lets ffmpeg flush — do it off the UI thread.
        var recorder = _recorder;
        var path = await Task.Run(() =>
        {
            var p = recorder.Stop();
            recorder.Dispose();
            return p;
        });

        Finished?.Invoke(path);
        Close();
    }

    private void OnDiscard(object? sender, RoutedEventArgs e)
    {
        if (_closing || _recorder is null) return;
        _closing = true;
        _tick.Stop();

        _recorder.Discard();
        _recorder.Dispose();
        Finished?.Invoke(null);
        Close();
    }

    private void SetBusy(string label)
    {
        if (_pauseButton is not null) _pauseButton.IsEnabled = false;
        foreach (var b in new[] { "StopButton", "DiscardButton" })
            if (this.FindControl<Button>(b) is { } btn) btn.IsEnabled = false;
        if (_elapsed is not null) _elapsed.Text = label;
    }

    /// <summary>Auto-place the bar just below or above the selection; if it fits neither, park it at the
    /// top of the screen.</summary>
    private void PositionOutsideRegion()
    {
        var centerX = _region.X + _region.Width / 2;
        var anchor = new PixelPoint(centerX, _region.Bottom);
        var screen = Screens.ScreenFromPoint(anchor) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;

        var wpx = (int)(Bounds.Width * screen.Scaling);
        var hpx = (int)(Bounds.Height * screen.Scaling);
        const int margin = 12;
        var area = screen.WorkingArea;

        var x = Math.Clamp(centerX - wpx / 2, area.X, Math.Max(area.X, area.Right - wpx));

        int y;
        if (_region.Bottom + margin + hpx <= area.Bottom)
            y = _region.Bottom + margin;                       // just below the selection
        else if (_region.Y - margin - hpx >= area.Y)
            y = _region.Y - margin - hpx;                      // just above the selection
        else
            y = area.Y + margin;                               // no room either side: top of screen

        Position = new PixelPoint(x, y);
    }
}
