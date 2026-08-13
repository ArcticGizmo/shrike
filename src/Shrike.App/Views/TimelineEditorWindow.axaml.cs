using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Shrike.App.Controls;
using Shrike.Core.Recording;

namespace Shrike.App.Views;

/// <summary>
/// The timeline editor: preview a recording, trim it (cut / keep-only / restore across the scrubber), and
/// hand it to the export dialog. Preview frames come from <see cref="FrameExtractor"/> (the bundled ffmpeg),
/// so there's no native video widget — scrubbing pulls the still at the cursor, and Play steps decoded
/// frames on a timer. All editing is on the in-memory <see cref="Timeline"/>; the source file is untouched
/// until export.
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

    private TimelineStrip _strip = null!;
    private Image _preview = null!;
    private TextBlock _timeLabel = null!;
    private TextBlock _keptLabel = null!;
    private Button _playButton = null!;

    private long _playheadSourceMs;
    private long _currentEditedMs;
    private long? _markInMs;
    private long? _markOutMs;
    private bool _playing;

    // Preview pump: coalesces rapid seek requests to one in-flight ffmpeg extraction.
    private long _wantMs = -1;
    private bool _extracting;
    private Bitmap? _currentFrame;

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
        _preview = this.FindControl<Image>("Preview")!;
        _timeLabel = this.FindControl<TextBlock>("TimeLabel")!;
        _keptLabel = this.FindControl<TextBlock>("KeptLabel")!;
        _playButton = this.FindControl<Button>("PlayButton")!;

        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playTimer.Tick += (_, _) => AdvancePlayback();

        _strip.Timeline = _timeline;
        _strip.Seeked += OnSeek;
        _strip.Scrubbing += OnScrub;
        _timeline.Changed += () => { _strip.Refresh(); UpdateLabels(); };

        Closed += (_, _) => { _cts.Cancel(); _playTimer.Stop(); };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdateLabels();
        RequestPreview(0);
        _ = LoadThumbnailsAsync(_cts.Token);
    }

    // ---- scrubbing / preview ----

    private void OnScrub(long sourceMs)
    {
        StopPlayback();
        _playheadSourceMs = sourceMs;
        _currentEditedMs = _timeline.SourceToEditedMs(sourceMs) ?? _currentEditedMs;
        RequestPreview(sourceMs);
        UpdateLabels();
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
                    _preview.Source = bmp;
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
        for (var i = 0; i < count && !ct.IsCancellationRequested; i++)
        {
            var ms = (long)((i + 0.5) / count * _source.DurationMs);
            var png = await Task.Run(() => _extractor.ExtractPng(ms, maxHeight: 76), ct).ConfigureAwait(true);
            if (png is null || ct.IsCancellationRequested) continue;
            try { _strip.AddThumbnail(ms, new Bitmap(new MemoryStream(png))); } catch { }
        }
    }

    // ---- playback ----

    private void OnPlayPause(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_playing) StopPlayback();
        else StartPlayback();
    }

    private void StartPlayback()
    {
        if (_timeline.KeptDurationMs <= 0) return;
        // Resume from the current spot; if we're sitting in a cut (or at the end), start over.
        _currentEditedMs = _timeline.SourceToEditedMs(_playheadSourceMs) ?? _currentEditedMs;
        if (_currentEditedMs >= _timeline.KeptDurationMs) _currentEditedMs = 0;
        _playing = true;
        _playButton.Content = "❚❚ Pause";
        _playTimer.Start();
    }

    private void StopPlayback()
    {
        if (!_playing) return;
        _playing = false;
        _playButton.Content = "▶ Play";
        _playTimer.Stop();
    }

    private void AdvancePlayback()
    {
        _currentEditedMs += (long)_playTimer.Interval.TotalMilliseconds;
        if (_currentEditedMs >= _timeline.KeptDurationMs)
        {
            _currentEditedMs = _timeline.KeptDurationMs;
            StopPlayback();
        }
        _playheadSourceMs = _timeline.EditedToSourceMs(_currentEditedMs);
        _strip.SetPlayhead(_playheadSourceMs);
        RequestPreview(_playheadSourceMs);
        UpdateLabels();
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
