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

    // Preset spotlight colours offered in the settings flyout.
    private static readonly string[] SwatchColors =
        ["#FFD24A", "#F5A524", "#EF4444", "#22C55E", "#3B82F6", "#EC4899", "#FFFFFF"];

    private readonly DispatcherTimer _tick;

    private PixelBounds _region;
    private Recorder? _recorder;
    private HudState _state = HudState.Setup;
    private SpotlightStyle _spotlightStyle;
    private bool _initializing;
    private bool _userMoved;   // once the user drags the HUD, we stop auto-following the region
    private bool _closing;

    private StackPanel? _setupPanel;
    private StackPanel? _recordingPanel;
    private ToggleButton? _hideCursorButton;
    private ToggleButton? _smoothCursorButton;
    private StackPanel? _spotlightGroup;
    private ToggleButton? _spotlightButton;
    private WrapPanel? _swatches;
    private Slider? _opacitySlider;
    private Slider? _sizeSlider;
    private TextBlock? _opacityValue;
    private TextBlock? _sizeValue;
    private TextBlock? _elapsed;
    private Button? _pauseButton;
    private Ellipse? _recDot;
    private readonly List<Button> _swatchButtons = [];

    /// <summary>Raised when the user presses Record in the setup state (region is final).</summary>
    public event Action? RecordRequested;

    /// <summary>Raised when the user backs out before recording starts.</summary>
    public event Action? CancelRequested;

    /// <summary>Raised once when recording ends: the saved MP4 path, or null if discarded.</summary>
    public event Action<string?>? Finished;

    /// <summary>Raised when the spotlight toggle flips (on/off).</summary>
    public event Action<bool>? SpotlightToggled;

    /// <summary>Raised when the "hide cursor" toggle flips. The argument is whether the cursor should
    /// appear <b>in the recording</b> (i.e. the inverse of "hide"), matching <c>CursorInRecording</c>.</summary>
    public event Action<bool>? CursorInRecordingToggled;

    /// <summary>Raised when the experimental "smooth cursor" toggle flips (on/off).</summary>
    public event Action<bool>? SmoothCursorToggled;

    /// <summary>Raised when the spotlight colour / opacity / size changes.</summary>
    internal event Action<SpotlightStyle>? SpotlightStyleChanged;

    // Parameterless ctor for the XAML designer only.
    public RecordingHudWindow() : this(default, false, new SpotlightStyle("#FFD24A", 0.30, 30), true, false) { }

    internal RecordingHudWindow(PixelBounds region, bool spotlightOn, SpotlightStyle spotlightStyle,
        bool cursorInRecording, bool smoothCursorOn)
    {
        _region = region;
        _spotlightStyle = spotlightStyle;
        InitializeComponent();

        _setupPanel = this.FindControl<StackPanel>("SetupPanel");
        _recordingPanel = this.FindControl<StackPanel>("RecordingPanel");
        _hideCursorButton = this.FindControl<ToggleButton>("HideCursorButton");
        _smoothCursorButton = this.FindControl<ToggleButton>("SmoothCursorButton");
        _spotlightGroup = this.FindControl<StackPanel>("SpotlightGroup");
        _spotlightButton = this.FindControl<ToggleButton>("SpotlightButton");
        _swatches = this.FindControl<WrapPanel>("Swatches");
        _opacitySlider = this.FindControl<Slider>("OpacitySlider");
        _sizeSlider = this.FindControl<Slider>("SizeSlider");
        _opacityValue = this.FindControl<TextBlock>("OpacityValue");
        _sizeValue = this.FindControl<TextBlock>("SizeValue");
        _elapsed = this.FindControl<TextBlock>("Elapsed");
        _pauseButton = this.FindControl<Button>("PauseButton");
        _recDot = this.FindControl<Ellipse>("RecDot");

        InitSpotlightControls(spotlightOn);

        // "Hide cursor" is simply the inverse of CursorInRecording.
        if (_hideCursorButton is not null) _hideCursorButton.IsChecked = !cursorInRecording;
        if (_smoothCursorButton is not null) _smoothCursorButton.IsChecked = smoothCursorOn;
        UpdateSmoothState();

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

    private void InitSpotlightControls(bool spotlightOn)
    {
        _initializing = true;

        if (_spotlightButton is not null) _spotlightButton.IsChecked = spotlightOn;

        // Colour swatches.
        foreach (var hex in SwatchColors)
        {
            var swatch = new Button
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.Parse(hex)),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Tag = hex,
            };
            swatch.Click += OnSwatch;
            _swatchButtons.Add(swatch);
            _swatches?.Children.Add(swatch);
        }
        HighlightSwatch(_spotlightStyle.Color);

        if (_opacitySlider is not null)
        {
            _opacitySlider.Value = _spotlightStyle.Opacity;
            _opacitySlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty) OnOpacityChanged();
            };
        }
        if (_sizeSlider is not null)
        {
            _sizeSlider.Value = _spotlightStyle.Radius;
            _sizeSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty) OnSizeChanged();
            };
        }

        UpdateStyleLabels();
        _initializing = false;
    }

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

    // ---- spotlight / cursor controls ----

    private void OnToggleSpotlight(object? sender, RoutedEventArgs e)
        => SpotlightToggled?.Invoke(_spotlightButton?.IsChecked ?? false);

    // The cursor is painted into frames independently of the spotlight, so the two toggles are free.
    private void OnToggleHideCursor(object? sender, RoutedEventArgs e)
        => CursorInRecordingToggled?.Invoke(!(_hideCursorButton?.IsChecked ?? false)); // report "in recording" = !hide

    private void OnToggleSmoothCursor(object? sender, RoutedEventArgs e)
    {
        UpdateSmoothState();
        SmoothCursorToggled?.Invoke(_smoothCursorButton?.IsChecked ?? false);
    }

    /// <summary>While smooth-cursor is on it supersedes the real cursor and the spotlight (the cursor is
    /// drawn in post over a clean plate), so grey those controls out to keep the HUD honest.</summary>
    private void UpdateSmoothState()
    {
        var smooth = _smoothCursorButton?.IsChecked ?? false;
        if (_hideCursorButton is not null) _hideCursorButton.IsEnabled = !smooth;
        if (_spotlightGroup is not null) _spotlightGroup.IsEnabled = !smooth;
    }

    private void OnSwatch(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex }) return;
        _spotlightStyle = _spotlightStyle with { Color = hex };
        HighlightSwatch(hex);
        RaiseStyleChanged();
    }

    private void OnOpacityChanged()
    {
        if (_opacitySlider is null) return;
        _spotlightStyle = _spotlightStyle with { Opacity = Math.Round(_opacitySlider.Value, 2) };
        UpdateStyleLabels();
        RaiseStyleChanged();
    }

    private void OnSizeChanged()
    {
        if (_sizeSlider is null) return;
        _spotlightStyle = _spotlightStyle with { Radius = (int)Math.Round(_sizeSlider.Value) };
        UpdateStyleLabels();
        RaiseStyleChanged();
    }

    private void HighlightSwatch(string hex)
    {
        foreach (var b in _swatchButtons)
            b.BorderBrush = b.Tag is string t && string.Equals(t, hex, StringComparison.OrdinalIgnoreCase)
                ? Brushes.White
                : Brushes.Transparent;
    }

    private void UpdateStyleLabels()
    {
        if (_opacityValue is not null)
            _opacityValue.Text = $"{(int)Math.Round(_spotlightStyle.Opacity * 100)}%";
        if (_sizeValue is not null)
            _sizeValue.Text = _spotlightStyle.Radius.ToString(CultureInfo.InvariantCulture);
    }

    private void RaiseStyleChanged()
    {
        if (_initializing) return;
        SpotlightStyleChanged?.Invoke(_spotlightStyle);
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
