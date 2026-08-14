using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shrike.App.Native;
using Shrike.Core.Capture;
using Shrike.Core.Recording;

namespace Shrike.App.Views;

/// <summary>
/// The single floating control bar for a recording, from setup to stop. It is born in the <b>setup</b>
/// state — a Record / Cancel pair beside the region the user is still adjusting — and, when recording
/// actually begins, swaps its contents in place to the <b>recording</b> state (live clock, enhance-mouse
/// toggle, pause / stop / discard). One window across the whole flow, so nothing pops in after the
/// countdown. It's draggable, sits just outside the recording region, and is excluded from capture so it
/// never lands in its own recording — essential for full-screen grabs where there's nowhere else to sit.
/// </summary>
public partial class RecordingHudWindow : Window
{
    private enum HudState { Setup, Recording }

    private readonly DispatcherTimer _tick;

    private PixelBounds _region;
    private Recorder? _recorder;
    private CursorGlowFrameSource? _glow;
    private Action<bool>? _onEnhanceChanged;
    private HudState _state = HudState.Setup;
    private bool _userMoved;   // once the user drags the HUD, we stop auto-following the region
    private bool _closing;

    private StackPanel? _setupPanel;
    private StackPanel? _recordingPanel;
    private TextBlock? _elapsed;
    private Button? _pauseButton;
    private ToggleButton? _enhanceButton;
    private Ellipse? _recDot;

    /// <summary>Raised when the user presses Record in the setup state (region is final).</summary>
    public event Action? RecordRequested;

    /// <summary>Raised when the user backs out before recording starts.</summary>
    public event Action? CancelRequested;

    /// <summary>Raised once when recording ends: the saved MP4 path, or null if discarded.</summary>
    public event Action<string?>? Finished;

    // Parameterless ctor for the XAML designer only.
    public RecordingHudWindow() : this(default) { }

    internal RecordingHudWindow(PixelBounds region)
    {
        _region = region;
        InitializeComponent();

        _setupPanel = this.FindControl<StackPanel>("SetupPanel");
        _recordingPanel = this.FindControl<StackPanel>("RecordingPanel");
        _elapsed = this.FindControl<TextBlock>("Elapsed");
        _pauseButton = this.FindControl<Button>("PauseButton");
        _enhanceButton = this.FindControl<ToggleButton>("EnhanceButton");
        _recDot = this.FindControl<Ellipse>("RecDot");

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => Refresh();
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
    internal void BeginRecording(Recorder recorder, CursorGlowFrameSource? glow, bool enhanceMouse, Action<bool>? onEnhanceChanged)
    {
        _recorder = recorder;
        _glow = glow;
        _onEnhanceChanged = onEnhanceChanged;
        _state = HudState.Recording;

        if (_enhanceButton is not null) _enhanceButton.IsChecked = enhanceMouse;
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
        if (e.Source is Visual v && v.GetSelfAndVisualAncestors().Any(a => a is Button or ToggleButton))
            return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _userMoved = true;
            BeginMoveDrag(e);
        }
    }

    private void OnRecord(object? sender, RoutedEventArgs e)
    {
        if (_state != HudState.Setup) return;
        // Lock the setup controls; the region is now final and the countdown owns the screen.
        if (_setupPanel is not null) _setupPanel.IsEnabled = false;
        RecordRequested?.Invoke();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (_state != HudState.Setup) return;
        CancelRequested?.Invoke();
    }

    private void OnToggleEnhance(object? sender, RoutedEventArgs e)
    {
        var on = _enhanceButton?.IsChecked ?? false;
        if (_glow is not null) _glow.Enabled = on;
        _onEnhanceChanged?.Invoke(on);
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

    /// <summary>Sit the bar just below the region (or above / at the screen edge if there's no room).</summary>
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
            y = _region.Bottom + margin;                       // below the region
        else if (_region.Y - margin - hpx >= area.Y)
            y = _region.Y - margin - hpx;                      // above the region
        else
            y = area.Bottom - hpx - margin;                    // last resort: screen bottom

        Position = new PixelPoint(x, y);
    }
}
