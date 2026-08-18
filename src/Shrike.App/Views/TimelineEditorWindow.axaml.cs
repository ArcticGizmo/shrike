using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Shrike.App.Controls;
using Shrike.Core.Recording;

namespace Shrike.App.Views;

/// <summary>
/// The timeline editor: preview a recording, trim it (cut / keep-only / restore across the scrubber), and
/// hand it to the export dialog. There's no native video widget — scrubbing pulls a crisp still at the
/// cursor via <see cref="FrameExtractor"/>, while Play streams frames from one persistent ffmpeg
/// (<see cref="FramePlayer"/>) for smooth real-time preview. All editing is on the in-memory
/// <see cref="Timeline"/>; the source file is untouched until export.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class TimelineEditorWindow : Window
{
    private readonly RecordingSource _source;
    private readonly string _ffmpegPath;
    private readonly FrameExtractor _extractor;
    private readonly Timeline _timeline;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _playTimer;

    private const int PlayHeightCap = 540;   // playback frames are softer/lighter; stills stay full-res

    private TimelineStrip _strip = null!;
    private PreviewSurface _preview = null!;
    private TextBlock _timeLabel = null!;
    private TextBlock _keptLabel = null!;
    private Button _playButton = null!;

    private long _playheadSourceMs;
    private long _currentEditedMs;
    private long? _markInMs;
    private long? _markOutMs;
    private bool _playing;

    // Streaming playback: one persistent ffmpeg feeds frames into this reused bitmap.
    private FramePlayer? _player;
    private WriteableBitmap? _playBitmap;

    // Scrub preview pump: coalesces rapid seek requests to one in-flight ffmpeg extraction.
    private long _wantMs = -1;
    private bool _extracting;
    private Bitmap? _currentFrame;

    // Smooth-cursor preview overlay + tuning: watch and dial in the smoothing/zoom the export renders.
    private MouseTrack? _smoothTrack;
    private SmoothedCursorTrack? _smoothed;
    private CursorSmoothing _smoothing = CursorSmoothing.Default;
    private ZoomConfig _zoom = ZoomConfig.Default;
    private double[]? _zoomCurve;
    private Slider? _minCutoffSlider;
    private Slider? _betaSlider;
    private TextBlock? _minCutoffValue;
    private TextBlock? _betaValue;
    private CheckBox? _zoomToggle;
    private Slider? _zoomSlider;
    private TextBlock? _zoomValue;

    // Parameterless ctor for the XAML designer only.
    public TimelineEditorWindow() : this(new RecordingSource("", 16, 16, 30, TimeSpan.FromSeconds(1)), "") { }

    internal TimelineEditorWindow(RecordingSource source, string ffmpegPath)
    {
        _source = source;
        _ffmpegPath = ffmpegPath;
        _extractor = new FrameExtractor(ffmpegPath, source.Path);
        _timeline = new Timeline(source);
        InitializeComponent();

        _strip = this.FindControl<TimelineStrip>("Strip")!;
        _preview = this.FindControl<PreviewSurface>("Preview")!;
        _timeLabel = this.FindControl<TextBlock>("TimeLabel")!;
        _keptLabel = this.FindControl<TextBlock>("KeptLabel")!;
        _playButton = this.FindControl<Button>("PlayButton")!;

        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playTimer.Tick += (_, _) => AdvancePlayback();

        _strip.Timeline = _timeline;
        _strip.Seeked += OnSeek;
        _strip.Scrubbing += OnScrub;
        // Any edit changes the kept ranges, so a running playback is now stale — stop and show a still.
        _timeline.Changed += () => { StopPlayback(showStill: true); _strip.Refresh(); UpdateLabels(); };
        // A cut/keep changes where the cursor is at each edited time — re-project the overlay to match.
        _timeline.Changed += ReprojectSmoothTrack;
        SetupSmoothingPanel();

        Closed += (_, _) => { _cts.Cancel(); _playTimer.Stop(); _player?.Dispose(); };

#if DEBUG
        // Dev affordance: reveal this recording (and its .track.json sidecar) in Explorer.
        if (this.FindControl<Button>("RevealButton") is { } reveal)
        {
            reveal.IsVisible = true;
            reveal.Click += (_, _) => RevealSourceFiles();
        }
#endif
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

#if DEBUG
    /// <summary>Debug-only: open Explorer with this recording selected, so its <c>.track.json</c> sidecar is
    /// visible right beside it (falls back to opening the working folder if the file has since gone).</summary>
    private void RevealSourceFiles()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(_source.Path)) return;
        try
        {
            var arg = File.Exists(_source.Path)
                ? $"/select,\"{_source.Path}\""
                : $"\"{Shrike.Core.AppStorage.RecordingsDirectory()}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", arg)
            {
                UseShellExecute = true,
            });
        }
        catch { /* best effort — dev convenience only */ }
    }
#endif

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdateLabels();
        RequestPreview(0);
        _ = LoadThumbnailsAsync(_cts.Token);
        LoadSmoothTrack();
    }

    // ---- smooth-cursor preview overlay + tuning ----

    private void LoadSmoothTrack()
    {
        try
        {
            var path = Shrike.Core.AppStorage.SidecarFor(_source.Path);
            _smoothTrack = File.Exists(path) ? MouseTrack.Load(path) : null;
        }
        catch { _smoothTrack = null; }

        // The tuning panel only makes sense for a clip that actually carries a track.
        if (this.FindControl<Border>("SmoothingPanel") is { } panel)
            panel.IsVisible = _smoothTrack is not null;

        ReprojectSmoothTrack();
    }

    private void ReprojectSmoothTrack()
    {
        if (_smoothTrack is null || _source.Width <= 0 || _source.Height <= 0)
        {
            _smoothed = null;
            _preview.SetCursor(null);
            return;
        }
        _smoothed = SmoothCursor.Project(_smoothTrack, _timeline, _source.Fps, _source.Width, _source.Height, _smoothing);
        _zoomCurve = AutoZoom.ZoomCurve(_smoothed.Clicks, _smoothed.Frames.Count, _smoothed.Fps, _zoom);
        UpdateCursorOverlay();
    }

    private void UpdateCursorOverlay()
    {
        if (_smoothed is null || _smoothed.IsEmpty) { _preview.SetCursor(null); _preview.SetViewport(null); return; }
        var i = Math.Clamp((int)Math.Round(_currentEditedMs * _smoothed.Fps / 1000.0), 0, _smoothed.Frames.Count - 1);
        var s = _smoothed.Frames[i];

        var z = _zoomCurve is { } zc && i < zc.Length ? zc[i] : 1.0;
        if (z > 1.0001)
        {
            // Crop the preview to the zoom viewport and place the cursor within that crop (0..1).
            var vp = AutoZoom.Viewport(z, s.X, s.Y, _source.Width, _source.Height);
            _preview.SetViewport(new Rect(vp.X / _source.Width, vp.Y / _source.Height, vp.Width / _source.Width, vp.Height / _source.Height));
            _preview.SetCursor(new Point((s.X - vp.X) / vp.Width, (s.Y - vp.Y) / vp.Height));
        }
        else
        {
            _preview.SetViewport(null);
            _preview.SetCursor(new Point(s.X / _source.Width, s.Y / _source.Height));
        }
    }

    private void SetupSmoothingPanel()
    {
        _minCutoffSlider = this.FindControl<Slider>("MinCutoffSlider");
        _betaSlider = this.FindControl<Slider>("BetaSlider");
        _minCutoffValue = this.FindControl<TextBlock>("MinCutoffValue");
        _betaValue = this.FindControl<TextBlock>("BetaValue");

        if (_minCutoffSlider is not null)
        {
            _minCutoffSlider.Value = _smoothing.MinCutoff;
            _minCutoffSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) OnSmoothingChanged();
            };
        }
        if (_betaSlider is not null)
        {
            _betaSlider.Value = _smoothing.Beta;
            _betaSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) OnSmoothingChanged();
            };
        }
        _zoomToggle = this.FindControl<CheckBox>("ZoomToggle");
        _zoomSlider = this.FindControl<Slider>("ZoomSlider");
        _zoomValue = this.FindControl<TextBlock>("ZoomValue");
        if (_zoomToggle is not null)
        {
            _zoomToggle.IsChecked = _zoom.Enabled;
            _zoomToggle.IsCheckedChanged += (_, _) => OnZoomChanged();
        }
        if (_zoomSlider is not null)
        {
            _zoomSlider.Value = _zoom.MaxZoom;
            _zoomSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) OnZoomChanged();
            };
        }
        if (this.FindControl<Button>("SmoothingReset") is { } reset)
            reset.Click += (_, _) => ResetSmoothing();

        UpdateSmoothingLabels();
    }

    private void OnSmoothingChanged()
    {
        var minCutoff = Math.Max(0.1, _minCutoffSlider?.Value ?? _smoothing.MinCutoff);
        var beta = Math.Max(0.0, _betaSlider?.Value ?? _smoothing.Beta);
        _smoothing = new CursorSmoothing(minCutoff, beta);
        UpdateSmoothingLabels();
        ReprojectSmoothTrack();
    }

    private void OnZoomChanged()
    {
        var enabled = _zoomToggle?.IsChecked ?? false;
        var max = Math.Max(1.0, _zoomSlider?.Value ?? _zoom.MaxZoom);
        _zoom = _zoom with { Enabled = enabled, MaxZoom = max };
        UpdateSmoothingLabels();
        ReprojectSmoothTrack();
    }

    private void ResetSmoothing()
    {
        _smoothing = CursorSmoothing.Default;
        _zoom = ZoomConfig.Default;
        if (_minCutoffSlider is not null) _minCutoffSlider.Value = _smoothing.MinCutoff;
        if (_betaSlider is not null) _betaSlider.Value = _smoothing.Beta;
        if (_zoomToggle is not null) _zoomToggle.IsChecked = _zoom.Enabled;
        if (_zoomSlider is not null) _zoomSlider.Value = _zoom.MaxZoom;
        UpdateSmoothingLabels();
        ReprojectSmoothTrack();
    }

    private void UpdateSmoothingLabels()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (_minCutoffValue is not null) _minCutoffValue.Text = _smoothing.MinCutoff.ToString("0.0#", inv);
        if (_betaValue is not null) _betaValue.Text = _smoothing.Beta.ToString("0.00#", inv);
        if (_zoomValue is not null) _zoomValue.Text = _zoom.MaxZoom.ToString("0.0#", inv) + "×";
    }

    // ---- scrubbing / preview ----

    private void OnScrub(long sourceMs)
    {
        StopPlayback();
        _playheadSourceMs = sourceMs;
        _currentEditedMs = _timeline.SourceToEditedMs(sourceMs) ?? _currentEditedMs;
        RequestPreview(sourceMs);
        UpdateLabels();
        UpdateCursorOverlay();
    }

    private void OnSeek(long sourceMs) => OnScrub(sourceMs);

    private async void RequestPreview(long sourceMs)
    {
        _wantMs = sourceMs;
        if (_extracting) return;
        _extracting = true;
        try
        {
            while (_wantMs >= 0 && !_cts.IsCancellationRequested)
            {
                var ms = _wantMs;
                _wantMs = -1;
                var png = await Task.Run(() => _extractor.ExtractPng(ms), _cts.Token).ConfigureAwait(true);
                if (png is null) continue;
                try
                {
                    var bmp = new Bitmap(new MemoryStream(png));
                    _preview.Show(bmp);
                    _currentFrame?.Dispose();
                    _currentFrame = bmp;
                }
                catch { /* undecodable frame — keep the last good one */ }
            }
        }
        catch (OperationCanceledException) { /* window closing */ }
        finally { _extracting = false; }
    }

    private async Task LoadThumbnailsAsync(CancellationToken ct)
    {
        const int count = 14;
        // One ffmpeg pass for the whole filmstrip — far faster than a spawn per thumbnail, and it leaves
        // the CPU free for the preview/Play extraction instead of starving it.
        var pngs = await Task.Run(() => _extractor.ExtractThumbnails(count, _source.DurationMs, 76), ct)
            .ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;

        for (var i = 0; i < pngs.Count; i++)
        {
            var ms = (long)((i + 0.5) / pngs.Count * _source.DurationMs);
            try { _strip.AddThumbnail(ms, new Bitmap(new MemoryStream(pngs[i]))); } catch { }
        }
    }

    // ---- playback ----

    private void OnPlayPause(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_playing) StopPlayback(showStill: true);
        else StartPlayback();
    }

    private void StartPlayback()
    {
        if (_timeline.KeptDurationMs <= 0) return;
        // Resume from the current edited position (kept authoritative by scrub + playback). Once we've
        // reached the end, Play restarts from the top rather than resuming on the final frame.
        if (_currentEditedMs >= _timeline.KeptDurationMs) _currentEditedMs = 0;

        var ranges = _timeline.KeptRangesFrom(_currentEditedMs);
        if (ranges.Count == 0) { _currentEditedMs = 0; ranges = _timeline.KeptRangesFrom(0); }
        if (ranges.Count == 0) return;

        var player = new FramePlayer(_ffmpegPath, _source);
        try { player.Start(ranges, Math.Min(_source.Height, PlayHeightCap), Math.Min(_source.Fps, 30)); }
        catch { player.Dispose(); return; }
        _player = player;

        EnsurePlayBitmap(player.Width, player.Height);
        _playTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / player.Fps);
        _playing = true;
        _playButton.Content = "❚❚ Pause";
        _playTimer.Start();
    }

    private void StopPlayback(bool showStill = false)
    {
        if (!_playing && _player is null) return;
        _playing = false;
        _playButton.Content = "▶ Play";
        _playTimer.Stop();
        _player?.Dispose();
        _player = null;
        if (showStill) RequestPreview(_playheadSourceMs);   // swap the soft play frame for a crisp still
    }

    // One timer tick = consume one streamed frame, advancing the edited clock by exactly one frame.
    private void AdvancePlayback()
    {
        var player = _player;
        if (player is null) return;

        var frame = player.TryTakeFrame();
        if (frame is null)
        {
            if (player.Ended) { _currentEditedMs = _timeline.KeptDurationMs; StopPlayback(showStill: true); }
            return;   // buffer underrun — just wait for the next tick, stays in sync
        }

        BlitFrame(frame);
        _currentEditedMs = Math.Min(_currentEditedMs + (long)(1000.0 / player.Fps), _timeline.KeptDurationMs);
        _playheadSourceMs = _timeline.EditedToSourceMs(_currentEditedMs);
        _strip.SetPlayhead(_playheadSourceMs);
        UpdateLabels();
        UpdateCursorOverlay();
    }

    private void EnsurePlayBitmap(int w, int h)
    {
        if (_playBitmap is { } b && b.PixelSize.Width == w && b.PixelSize.Height == h) return;
        _playBitmap?.Dispose();
        _playBitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
    }

    private void BlitFrame(byte[] bgra)
    {
        var bmp = _playBitmap;
        if (bmp is null) return;
        using (var fb = bmp.Lock())
        {
            var rowBytes = bmp.PixelSize.Width * 4;
            if (fb.RowBytes == rowBytes)
            {
                Marshal.Copy(bgra, 0, fb.Address, Math.Min(bgra.Length, rowBytes * bmp.PixelSize.Height));
            }
            else
            {
                for (var row = 0; row < bmp.PixelSize.Height; row++)   // respect any stride padding
                    Marshal.Copy(bgra, row * rowBytes, fb.Address + row * fb.RowBytes, rowBytes);
            }
        }
        _preview.Show(bmp);
    }

    // ---- editing ----

    private void OnMarkIn(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _markInMs = _playheadSourceMs;
        if (_markOutMs <= _markInMs) _markOutMs = null;
        _strip.MarkInMs = _markInMs; _strip.MarkOutMs = _markOutMs;
        _strip.Refresh();
    }

    private void OnMarkOut(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _markOutMs = _playheadSourceMs;
        if (_markInMs >= _markOutMs) _markInMs = null;
        _strip.MarkInMs = _markInMs; _strip.MarkOutMs = _markOutMs;
        _strip.Refresh();
    }

    private void OnCut(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_markInMs is { } a && _markOutMs is { } b) _timeline.Cut(a, b);
        else _timeline.DeleteSegmentAt(_playheadSourceMs);   // no marks → drop the span under the playhead
        ClearMarks();
    }

    private void OnKeepOnly(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_markInMs is { } a && _markOutMs is { } b) _timeline.KeepOnly(a, b);
        ClearMarks();
    }

    private void OnRestoreAt(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _timeline.RestoreSegmentAt(_playheadSourceMs);
        ClearMarks();
    }

    private void OnResetAll(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _timeline.RestoreAll();
        ClearMarks();
    }

    private void ClearMarks()
    {
        _markInMs = _markOutMs = null;
        _strip.MarkInMs = _strip.MarkOutMs = null;
        _strip.Refresh();
        UpdateLabels();
    }

    // ---- export ----

    private async void OnExport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        StopPlayback();
        if (!_timeline.HasKeptContent) return;
        var dlg = new ExportDialog(_source, _timeline, _ffmpegPath);
        dlg.ConfigureSmoothCursor(_smoothing, _zoom); // carry the tuned preview settings into the export
        await dlg.ShowDialog(this);
    }

    // ---- labels ----

    private void UpdateLabels()
    {
        _timeLabel.Text = $"{Fmt(_currentEditedMs)} / {Fmt(_timeline.KeptDurationMs)}";
        var cuts = _timeline.Segments.Count(s => !s.Kept);
        var cutText = cuts == 0 ? "no cuts" : cuts == 1 ? "1 cut" : $"{cuts} cuts";
        _keptLabel.Text = $"{cutText} · source {Fmt(_source.DurationMs)}";
    }

    private static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }
}
