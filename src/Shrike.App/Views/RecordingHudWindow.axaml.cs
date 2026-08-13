using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Shrike.App.Native;
using Shrike.Core.Capture;
using Shrike.Core.Recording;

namespace Shrike.App.Views;

/// <summary>
/// The floating recording HUD: a live elapsed clock plus pause/resume, stop, and discard. Drives the
/// <see cref="Recorder"/> and raises <see cref="Finished"/> with the saved path (or null on discard).
/// Positions itself just outside the recording region so it isn't caught in the (GDI) capture, and is
/// born on the current desktop like the other Shrike surfaces.
/// </summary>
public partial class RecordingHudWindow : Window
{
    private readonly Recorder _recorder;
    private readonly PixelBounds _region;
    private readonly DispatcherTimer _tick;

    private TextBlock? _elapsed;
    private Button? _pauseButton;
    private Ellipse? _recDot;
    private bool _closing;

    /// <summary>Raised once when recording ends: the saved MP4 path, or null if discarded.</summary>
    public event Action<string?>? Finished;

    // Parameterless ctor for the XAML designer only.
    public RecordingHudWindow() : this(null!, default) { }

    internal RecordingHudWindow(Recorder recorder, PixelBounds region)
    {
        _recorder = recorder;
        _region = region;
        InitializeComponent();

        _elapsed = this.FindControl<TextBlock>("Elapsed");
        _pauseButton = this.FindControl<Button>("PauseButton");
        _recDot = this.FindControl<Ellipse>("RecDot");

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => Refresh();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PositionOutsideRegion();

        // The whole point: keep the HUD out of the recording (essential for full-screen captures, where
        // there's nowhere outside the region to sit). The window stays visible to the user regardless.
        if (OperatingSystem.IsWindows())
            WindowExclusion.Hide(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);

        _tick.Start();
        Refresh();
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
        if (_recorder.State == RecordingState.Paused) _recorder.Resume();
        else if (_recorder.State == RecordingState.Recording) _recorder.Pause();
        Refresh();
    }

    private async void OnStop(object? sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        _tick.Stop();
        SetBusy("Saving…");

        // Finalising joins the capture loop and lets ffmpeg flush — do it off the UI thread.
        var path = await Task.Run(() =>
        {
            var p = _recorder.Stop();
            _recorder.Dispose();
            return p;
        });

        Finished?.Invoke(path);
        Close();
    }

    private void OnDiscard(object? sender, RoutedEventArgs e)
    {
        if (_closing) return;
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
