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
    private ZoomTrack _authoredZoom = ZoomTrack.Empty;          // user-placed zoom events (the edit document)
    private double _cursorSize = 1.0;
    private bool _cursorRipple = true;
    private bool _showCursor = true;              // per-clip: draw the synthetic cursor (default from capture)
    private CheckBox? _cursorToggle;
    private Slider? _smoothnessSlider;
    private TextBlock? _smoothnessValue;
    private Slider? _sizeSlider;
    private TextBlock? _sizeValue;
    private CheckBox? _rippleToggle;

    // Unified effects lane + (zoom) inspector.
    private readonly List<EffectEvent> _effects = new();
    private EffectsLane? _effectsLane;
    private Border? _zoomPropsPane;
    private NumericUpDown? _zoomAmountInput;
    private NumericUpDown? _easeInInput;
    private NumericUpDown? _easeOutInput;
    private bool _suppressZoomInspector;

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

        if (this.FindControl<TimeRuler>("TimeRuler") is { } ruler) ruler.Timeline = _timeline;

        _strip.Timeline = _timeline;
        _strip.Seeked += OnSeek;
        _strip.Scrubbing += OnScrub;
        // Any edit changes the kept ranges, so a running playback is now stale — stop and show a still.
        _timeline.Changed += () => { StopPlayback(showStill: true); _strip.Refresh(); UpdateLabels(); };
        // A cut/keep changes where the cursor is at each edited time — re-project the overlay to match.
        _timeline.Changed += ReprojectSmoothTrack;
        SeedTuningFromSettings();
        SetupSmoothingPanel();
        SetupEffectsLane();

        Closed += (_, _) => { PersistTuning(); PersistEdit(); _cts.Cancel(); _playTimer.Stop(); _player?.Dispose(); };

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

        // The authored edit document: zoom events + the cursor-shown default. Empty zoom means no zoom yet.
        var edit = ClipEdit.Load(Shrike.Core.AppStorage.EditDocFor(_source.Path));
        _authoredZoom = edit.Zoom;
        _showCursor = edit.ShowCursor;
        if (_cursorToggle is not null) _cursorToggle.IsChecked = _showCursor;

        // Seed the lane's editable list from the loaded zoom events (the only kind persisted so far), and mark
        // where clicks fired (snap targets). Other effect kinds are placeable but session-only until the v2
        // edit-doc format (M3) stores them.
        _effects.Clear();
        _effects.AddRange(_authoredZoom.Events.Select(ZoomEffect.FromZoomEvent));
        if (_effectsLane is not null)
        {
            _effectsLane.Timeline = _timeline;
            _effectsLane.Events = _effects;
            _effectsLane.ClickMarks = _smoothTrack?.Clicks.Where(c => c.Down).Select(c => (long)c.TMs).ToArray() ?? [];
            _effectsLane.Select(-1);
            _effectsLane.Refresh();
        }

        // The tuning panel + effects lane only make sense for a clip that actually carries a track.
        var hasTrack = _smoothTrack is not null;
        if (this.FindControl<Border>("SmoothingPanel") is { } panel) panel.IsVisible = hasTrack;
        if (this.FindControl<StackPanel>("EffectsPanel") is { } effectsPanel) effectsPanel.IsVisible = hasTrack;

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
        UpdateCursorOverlay();
    }

    // ---- effects authoring ----

    private void SetupEffectsLane()
    {
        _effectsLane = this.FindControl<EffectsLane>("EffectsLane");
        _zoomPropsPane = this.FindControl<Border>("ZoomPropsPane");
        _zoomAmountInput = this.FindControl<NumericUpDown>("ZoomAmountInput");
        _easeInInput = this.FindControl<NumericUpDown>("EaseInInput");
        _easeOutInput = this.FindControl<NumericUpDown>("EaseOutInput");

        if (_effectsLane is not null)
        {
            _effectsLane.Timeline = _timeline;
            _effectsLane.Changed += OnEffectsChanged;
            _effectsLane.SelectionChanged += OnEffectSelectionChanged;
            _effectsLane.AddRequested += OnAddEffect;
            _effectsLane.DeleteRequested += DeleteEffectAt;
        }
        _preview.TargetBoxDrawn += OnTargetBoxDrawn;
        if (this.FindControl<Button>("AddEffectButton") is { } add)
            add.Click += (_, _) => _effectsLane?.ShowAddMenu(add, atPointer: false, _playheadSourceMs, hitIndex: -1);
        if (this.FindControl<Button>("DeleteZoomButton") is { } del)
            del.Click += (_, _) => DeleteSelectedEffect();
        if (_zoomAmountInput is not null) _zoomAmountInput.ValueChanged += (_, _) => OnZoomPropsChanged();
        if (_easeInInput is not null) _easeInInput.ValueChanged += (_, _) => OnZoomPropsChanged();
        if (_easeOutInput is not null) _easeOutInput.ValueChanged += (_, _) => OnZoomPropsChanged();
    }

    // The zoom effects, as the track the preview + export consume. Non-zoom effects don't affect framing.
    private ZoomTrack ZoomTrackFromEffects()
        => new(_effects.OfType<ZoomEffect>().Select(z => z.ToZoomEvent()).ToList());

    // The selected effect if it's a zoom (the only kind with an inspector + aim box today), else null.
    private ZoomEffect? SelectedZoomEffect()
        => _effectsLane is { SelectedIndex: var i } && i >= 0 && i < _effects.Count && _effects[i] is ZoomEffect z
            ? z : null;

    // The lane edited an effect's timing (drag/resize) — rebuild the zoom track and refresh the preview.
    private void OnEffectsChanged()
    {
        _authoredZoom = ZoomTrackFromEffects();
        UpdateCursorOverlay();
    }

    private void OnEffectSelectionChanged(int index)
    {
        var zoom = index >= 0 && index < _effects.Count ? _effects[index] as ZoomEffect : null;
        // The inspector + aim box are zoom-only today; other kinds have no editor yet (their milestone adds one).
        if (_zoomPropsPane is not null) _zoomPropsPane.IsVisible = zoom is not null;
        _preview.AimMode = zoom is not null;
        _preview.SetTargetBox(zoom is not null ? EventBox(zoom) : null);

        if (zoom is not null)
        {
            _suppressZoomInspector = true;
            if (_zoomAmountInput is not null) _zoomAmountInput.Value = (decimal)zoom.Zoom;
            if (_easeInInput is not null) _easeInInput.Value = (decimal)(zoom.EaseInMs / 1000.0);
            if (_easeOutInput is not null) _easeOutInput.Value = (decimal)(zoom.EaseOutMs / 1000.0);
            _suppressZoomInspector = false;
        }
        UpdateCursorOverlay(); // aiming shows the full frame; deselect restores the zoom view
    }

    // The selected event's target as a normalised box (a square in normalised coords: side = 1/zoom).
    private static Rect EventBox(ZoomEffect ev)
    {
        var side = Math.Clamp(1.0 / Math.Max(1.05, ev.Zoom), 0.05, 1.0);
        var x = Math.Clamp(ev.CenterX - side / 2, 0, 1 - side);
        var y = Math.Clamp(ev.CenterY - side / 2, 0, 1 - side);
        return new Rect(x, y, side, side);
    }

    // The user dragged a box on the preview → derive focus (centre) + zoom (fit the box), aspect enforced.
    private void OnTargetBoxDrawn(Rect norm)
    {
        if (_effectsLane is null) return;
        var i = _effectsLane.SelectedIndex;
        if (i < 0 || i >= _effects.Count || _effects[i] is not ZoomEffect ev) return;

        var side = Math.Max(norm.Width, norm.Height);
        var zoom = Math.Clamp(1.0 / Math.Max(0.01, side), 1.05, 3.0);
        var cx = Math.Clamp(norm.X + norm.Width / 2, 0, 1);
        var cy = Math.Clamp(norm.Y + norm.Height / 2, 0, 1);
        _effects[i] = ev with { Zoom = zoom, CenterX = cx, CenterY = cy };

        _suppressZoomInspector = true;
        if (_zoomAmountInput is not null) _zoomAmountInput.Value = (decimal)zoom;
        _suppressZoomInspector = false;
        _preview.SetTargetBox(EventBox((ZoomEffect)_effects[i])); // snap the shown box to the aspect-correct square
        OnEffectsChanged();
        _effectsLane.Refresh();
    }

    // The right-pane inputs changed — apply zoom + independent ease-in/out to the selected zoom effect.
    private void OnZoomPropsChanged()
    {
        if (_suppressZoomInspector || _effectsLane is null) return;
        var i = _effectsLane.SelectedIndex;
        if (i < 0 || i >= _effects.Count || _effects[i] is not ZoomEffect ev) return;
        _effects[i] = ev with
        {
            Zoom = Math.Max(1.05, (double)(_zoomAmountInput?.Value ?? (decimal)ev.Zoom)),
            EaseInMs = (long)Math.Round((double)(_easeInInput?.Value ?? 0) * 1000),
            EaseOutMs = (long)Math.Round((double)(_easeOutInput?.Value ?? 0) * 1000),
        };
        _preview.SetTargetBox(EventBox((ZoomEffect)_effects[i])); // zoom changed → the target box resizes to match
        OnEffectsChanged();
        _effectsLane.Refresh();
    }

    // Add a new effect of a given kind at a source time, with sensible per-kind defaults, and select it.
    private void OnAddEffect(EffectKind kind, long sourceMs)
    {
        if (_effectsLane is null) return;
        var full = _timeline.DurationMs;
        var wanted = kind == EffectKind.Ripple ? 600 : kind == EffectKind.Zoom || kind == EffectKind.Spotlight ? 1500 : 2000;
        var dur = Math.Min(wanted, full);
        var start = Math.Clamp(sourceMs, 0, Math.Max(0, full - dur));
        var end = Math.Min(full, start + dur);

        var s = Services.SettingsService.Instance?.Current ?? Shrike.Core.Settings.AppSettings.Default;
        EffectEvent effect = kind switch
        {
            EffectKind.Zoom => new ZoomEffect(start, end, 400, 400,
                CursorNormAtSource(sourceMs).X, CursorNormAtSource(sourceMs).Y, 1.8),
            EffectKind.Spotlight => new SpotlightEffect(start, end, 250, 250, s.SpotlightColor, s.SpotlightOpacity, s.SpotlightRadius),
            EffectKind.Ripple => new RippleEffect(start, end),
            EffectKind.Visibility => new VisibilityEffect(start, end, Visible: false), // a "hide" span (default is shown)
            EffectKind.Canvas => new CanvasEffect(start, end, 200, 200, CanvasSpace.Content),
            _ => new ZoomEffect(start, end, 400, 400, 0.5, 0.5, 1.8),
        };
        _effects.Add(effect);
        OnEffectsChanged();
        _effectsLane.Refresh();
        _effectsLane.Select(_effects.Count - 1);
    }

    private void DeleteSelectedEffect() => DeleteEffectAt(_effectsLane?.SelectedIndex ?? -1);

    private void DeleteEffectAt(int i)
    {
        if (_effectsLane is null || i < 0 || i >= _effects.Count) return;
        _effects.RemoveAt(i);
        _effectsLane.Select(-1);
        OnEffectsChanged();
        _effectsLane.Refresh();
    }

    // Normalised cursor position at a source time — the default focus for a new zoom event.
    private (double X, double Y) CursorNormAtSource(long sourceMs)
    {
        if (_smoothed is null || _smoothed.IsEmpty || _source.Width <= 0 || _source.Height <= 0) return (0.5, 0.5);
        var editedMs = _timeline.SourceToEditedMs(sourceMs) ?? _currentEditedMs;
        var i = Math.Clamp((int)Math.Round(editedMs * _smoothed.Fps / 1000.0), 0, _smoothed.Frames.Count - 1);
        var s = _smoothed.Frames[i];
        return (Math.Clamp(s.X / _source.Width, 0, 1), Math.Clamp(s.Y / _source.Height, 0, 1));
    }

    private void UpdateCursorOverlay()
    {
        if (_smoothed is null || _smoothed.IsEmpty)
        {
            _preview.SetCursor(null); _preview.SetViewport(null); _preview.SetRipples([]);
            return;
        }
        var i = Math.Clamp((int)Math.Round(_currentEditedMs * _smoothed.Fps / 1000.0), 0, _smoothed.Frames.Count - 1);
        var s = _smoothed.Frames[i];

        // The zoom crop at this frame — resolved for just this frame (cheap; no whole-clip array). The full
        // frame when there's no zoom, and always the full frame while aiming a selected event so the whole
        // picture is visible to box a target on.
        var aiming = SelectedZoomEffect() is not null;
        var vp = !aiming && !_authoredZoom.IsEmpty
            ? _authoredZoom.ViewportAt(_timeline.EditedToSourceMs((long)(i * 1000.0 / _smoothed.Fps)), _source.Width, _source.Height)
            : new ZoomViewport(0, 0, _source.Width, _source.Height);
        var zoomed = vp.Width < _source.Width - 0.5 || vp.Height < _source.Height - 0.5;

        // Position a point (export px) as a fraction of the displayed crop — matches the export's viewport map.
        Point Norm(double x, double y) => new((x - vp.X) / vp.Width, (y - vp.Y) / vp.Height);

        _preview.SetViewport(zoomed
            ? new Rect(vp.X / _source.Width, vp.Y / _source.Height, vp.Width / _source.Width, vp.Height / _source.Height)
            : null);
        // Zoom still applies when the cursor is hidden — only the cursor + ripples drop out.
        _preview.SetCursor(_showCursor ? Norm(s.X, s.Y) : null);
        _preview.SetRipples(_showCursor ? ActiveRipples(i, vp) : []);
    }

    /// <summary>The click ripples live at frame <paramref name="i"/>, mirrored from the export's cursor compositor
    /// (same lifetime, radii, and viewport mapping) so the preview matches the file.</summary>
    private IReadOnlyList<PreviewSurface.PreviewRipple> ActiveRipples(int i, ZoomViewport vp)
    {
        if (!_cursorRipple || _smoothed is null || _smoothed.IsEmpty || _source.Height <= 0)
            return [];

        var style = CursorStyle.ForExport(_source.Height, _cursorSize, _cursorRipple);
        var rippleFrames = Math.Max(1, (int)Math.Round(style.RippleSeconds * _smoothed.Fps));
        var ripples = new List<PreviewSurface.PreviewRipple>();
        foreach (var click in _smoothed.Clicks)
        {
            var age = i - click.FrameIndex;
            if (age < 0 || age >= rippleFrames) continue;
            var p = age / (double)rippleFrames;
            var c = _smoothed.Frames[Math.Clamp(click.FrameIndex, 0, _smoothed.Frames.Count - 1)];
            var radiusPx = style.RippleStartRadius + p * (style.RippleEndRadius - style.RippleStartRadius);
            ripples.Add(new PreviewSurface.PreviewRipple(
                Center: new Point((c.X - vp.X) / vp.Width, (c.Y - vp.Y) / vp.Height),
                RadiusFraction: radiusPx / _source.Height,
                ThicknessFraction: style.RippleThickness / _source.Height,
                Alpha: (1 - p) * style.RipplePeakAlpha));
        }
        return ripples;
    }

    /// <summary>Persist the authored edit document (zoom events) next to the clip, so it survives restart and
    /// re-export. Only for a clip that carries a track; an empty edit removes any stale sidecar.</summary>
    private void PersistEdit()
    {
        if (_smoothTrack is null || string.IsNullOrEmpty(_source.Path)) return;
        try { new ClipEdit(_authoredZoom, _showCursor).Save(Shrike.Core.AppStorage.EditDocFor(_source.Path)); }
        catch { /* best effort — never block closing on a failed save */ }
    }

    /// <summary>Start the editor from the persisted tuning so a dialled-in look carries across sessions.</summary>
    private void SeedTuningFromSettings()
    {
        var s = Services.SettingsService.Instance?.Current ?? Shrike.Core.Settings.AppSettings.Default;
        _smoothing = CursorSmoothing.FromSmoothness(s.CursorSmoothness);
        _cursorSize = s.CursorSize;
        _cursorRipple = s.CursorRippleEnabled;
    }

    /// <summary>Save the current smoothing/zoom back to settings (on close), but only for a clip that actually
    /// carries a track — so editing a plain recording never rewrites the smooth-cursor defaults.</summary>
    private void PersistTuning()
    {
        if (_smoothTrack is null) return;
        var svc = Services.SettingsService.Instance;
        if (svc is null) return;
        svc.Update(svc.Current with
        {
            CursorSmoothness = _smoothing.Smoothness,
            CursorSize = _cursorSize,
            CursorRippleEnabled = _cursorRipple,
        });
    }

    private void SetupSmoothingPanel()
    {
        _smoothnessSlider = this.FindControl<Slider>("SmoothnessSlider");
        _smoothnessValue = this.FindControl<TextBlock>("SmoothnessValue");

        if (_smoothnessSlider is not null)
        {
            _smoothnessSlider.Value = _smoothing.Smoothness * 100.0;
            _smoothnessSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) OnSmoothingChanged();
            };
        }
        _cursorToggle = this.FindControl<CheckBox>("CursorToggle");
        if (_cursorToggle is not null)
        {
            _cursorToggle.IsChecked = _showCursor;
            _cursorToggle.IsCheckedChanged += (_, _) => OnCursorLookChanged();
        }
        _sizeSlider = this.FindControl<Slider>("SizeSlider");
        _sizeValue = this.FindControl<TextBlock>("SizeValue");
        _rippleToggle = this.FindControl<CheckBox>("RippleToggle");
        if (_sizeSlider is not null)
        {
            _sizeSlider.Value = _cursorSize;
            _sizeSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) OnCursorLookChanged();
            };
        }
        if (_rippleToggle is not null)
        {
            _rippleToggle.IsChecked = _cursorRipple;
            _rippleToggle.IsCheckedChanged += (_, _) => OnCursorLookChanged();
        }
        if (this.FindControl<Button>("SmoothingReset") is { } reset)
            reset.Click += (_, _) => ResetSmoothing();

        UpdateSmoothingLabels();
        UpdateCursorScale();
    }

    private void OnCursorLookChanged()
    {
        _showCursor = _cursorToggle?.IsChecked ?? true;
        _cursorSize = Math.Clamp(_sizeSlider?.Value ?? _cursorSize, 0.5, 2.0);
        _cursorRipple = _rippleToggle?.IsChecked ?? true;
        UpdateSmoothingLabels();
        UpdateCursorScale();
        UpdateCursorOverlay(); // refresh cursor/ripple visibility at the current frame
    }

    /// <summary>Keep the previewed cursor the same relative size as the export renders it (WYSIWYG).</summary>
    private void UpdateCursorScale()
    {
        if (_source.Height <= 0) return;
        var frac = CursorStyle.ForExport(_source.Height, _cursorSize, _cursorRipple).Height / (double)_source.Height;
        _preview.SetCursorScale(frac);
    }

    private void OnSmoothingChanged()
    {
        var smoothness = (_smoothnessSlider?.Value ?? _smoothing.Smoothness * 100.0) / 100.0;
        _smoothing = CursorSmoothing.FromSmoothness(smoothness);
        UpdateSmoothingLabels();
        ReprojectSmoothTrack();
    }

    private void ResetSmoothing()
    {
        _smoothing = CursorSmoothing.FromSmoothness(CursorSmoothing.DefaultSmoothness);
        _cursorSize = 1.0;
        _cursorRipple = true;
        if (_smoothnessSlider is not null) _smoothnessSlider.Value = _smoothing.Smoothness * 100.0;
        if (_sizeSlider is not null) _sizeSlider.Value = _cursorSize;
        if (_rippleToggle is not null) _rippleToggle.IsChecked = _cursorRipple;
        UpdateSmoothingLabels();
        UpdateCursorScale();
        ReprojectSmoothTrack();
    }

    private void UpdateSmoothingLabels()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (_smoothnessValue is not null)
            _smoothnessValue.Text = Math.Round(_smoothing.Smoothness * 100.0).ToString("0", inv) + "%";
        if (_sizeValue is not null) _sizeValue.Text = _cursorSize.ToString("0.0#", inv) + "×";
    }

    // ---- scrubbing / preview ----

    private void OnScrub(long sourceMs)
    {
        StopPlayback();
        _playheadSourceMs = sourceMs;
        _currentEditedMs = _timeline.SourceToEditedMs(sourceMs) ?? _currentEditedMs;
        _effectsLane?.SetPlayhead(sourceMs);
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

    private void OnPlayPause(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => TogglePlayPause();

    private void TogglePlayPause()
    {
        if (_playing) StopPlayback(showStill: true);
        else StartPlayback();
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || FocusManager?.GetFocusedElement() is TextBox) return; // don't steal keys from an input

        // Space toggles play/pause.
        if (e.Key == Avalonia.Input.Key.Space)
        {
            TogglePlayPause();
            e.Handled = true;
            return;
        }

        // Delete / nudge the selected effect (arrow keys shift it in source time).
        var sel = _effectsLane?.SelectedIndex ?? -1;
        if (sel < 0 || sel >= _effects.Count) return;
        if (e.Key is Avalonia.Input.Key.Delete or Avalonia.Input.Key.Back)
        {
            DeleteEffectAt(sel);
            e.Handled = true;
        }
        else if (e.Key is Avalonia.Input.Key.Left or Avalonia.Input.Key.Right)
        {
            var step = (e.KeyModifiers & Avalonia.Input.KeyModifiers.Shift) != 0 ? 500 : 100;
            NudgeEffect(sel, e.Key == Avalonia.Input.Key.Left ? -step : step);
            e.Handled = true;
        }
    }

    // Shift the selected effect by delta ms in source time, clamped to the clip (keeps its duration).
    private void NudgeEffect(int i, long deltaMs)
    {
        if (_effectsLane is null || i < 0 || i >= _effects.Count) return;
        var ev = _effects[i];
        var dur = ev.EndMs - ev.StartMs;
        var start = Math.Clamp(ev.StartMs + deltaMs, 0, Math.Max(0, _timeline.DurationMs - dur));
        _effects[i] = ev with { StartMs = start, EndMs = start + dur };
        OnEffectsChanged();
        _effectsLane.Refresh();
        if (_effects[i] is ZoomEffect z) _preview.SetTargetBox(EventBox(z));
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
        _effectsLane?.SetPlayhead(_playheadSourceMs);
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
        PersistEdit(); // make sure the authored zoom is on disk before we (and the export) read it
        var dlg = new ExportDialog(_source, _timeline, _ffmpegPath);
        // Carry the tuned preview settings + authored zoom into the export so the file matches the preview.
        dlg.ConfigureSmoothCursor(_smoothing, _cursorSize, _cursorRipple, _showCursor, _authoredZoom);
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
