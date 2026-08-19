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
using Shrike.Core.Annotations;
using Shrike.Core.Capture;
using Shrike.Core.Imaging;
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
    private TimeRuler? _ruler;
    private PreviewSurface _preview = null!;
    private TextBlock _timeLabel = null!;
    private TextBlock _keptLabel = null!;
    private Button _playButton = null!;

    private long _playheadSourceMs;
    private long _currentEditedMs;
    private long _viewStartMs;
    private long _viewEndMs;   // 0 until initialised → full-clip view
    private bool _playing;
    private Border? _selectionBar;

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
    private ZoomTrack _authoredZoom = ZoomTrack.Empty;          // zoom events resolved from _effects (for preview/export)
    private double _cursorSize = 1.0;
    private bool _rippleDefaultOn = true;         // seed a full-length ripple for new/migrated clips (from settings)
    private Slider? _smoothnessSlider;
    private TextBlock? _smoothnessValue;
    private Slider? _sizeSlider;
    private TextBlock? _sizeValue;

    // Unified effects lane + the always-present properties pane.
    private readonly List<EffectEvent> _effects = new();
    private EffectsLane? _effectsLane;
    private Border? _propsPane;
    private TextBlock? _paneHeader;
    private TextBlock? _paneEmpty;
    private Control? _zoomEditor;
    private Control? _spotlightEditor;
    private Control? _visibilityEditor;
    private Control? _timingEditor;
    private NumericUpDown? _startInput;
    private NumericUpDown? _endInput;
    private Button? _deleteButton;
    // Segment (strip span) editor.
    private Control? _segmentEditor;
    private NumericUpDown? _segStartInput;
    private NumericUpDown? _segEndInput;
    private CheckBox? _segKeepToggle;
    private Button? _removeSplitButton;
    private (long Start, long End)? _selSeg;
    private NumericUpDown? _zoomAmountInput;
    private NumericUpDown? _easeInInput;
    private NumericUpDown? _easeOutInput;
    private TextBox? _spotlightColorInput;
    private Slider? _spotlightOpacityInput;
    private NumericUpDown? _spotlightRadiusInput;
    private CheckBox? _visibilityInput;
    private bool _suppressInspector;   // guards inspector editors from firing during programmatic seeding

    // Inline canvas (drawing) editing over the preview.
    private AnnotationSurface? _canvasSurface;
    private Control? _canvasEditor;
    private Control? _canvasTools;
    private Avalonia.Controls.Primitives.ToggleButton? _canvasEditToggle;
    private CheckBox? _canvasScreenSpace;
    private ComboBox? _canvasAnimCombo;
    private AnnotationDocument? _canvasDoc;
    private int _editingCanvasIndex = -1;
    private bool _suppressCanvasToggle;
    private readonly Dictionary<IReadOnlyList<Annotation>, Bitmap> _canvasLayerCache = new(ReferenceEqualityComparer.Instance);

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

        _ruler = this.FindControl<TimeRuler>("TimeRuler");
        if (_ruler is not null)
        {
            _ruler.Timeline = _timeline;
            _ruler.Scrubbing += OnScrub;   // the ruler is draggable to scrub, like the filmstrip
            _ruler.Seeked += OnSeek;
            _ruler.ZoomRequested += OnTimelineZoom;
            _ruler.PanRequested += OnTimelinePan;
        }

        _strip.Timeline = _timeline;
        _strip.Seeked += OnSeek;                 // a plain click on the strip seeks
        _strip.SegmentSelected += OnSegmentSelected; // double-click a span → edit it in the pane
        _strip.RangeSelected += OnRangeSelected; // drag on the body selects a quick range to cut/keep
        _strip.SelectionCleared += OnSelectionCleared;
        _strip.TrimHeadTo += OnTrimHead;         // drag the end handles to trim head/tail
        _strip.TrimTailTo += OnTrimTail;
        _strip.BoundaryMoved += (from, to) => { _timeline.MoveBoundary(from, to); UpdateLabels(); };
        _strip.SplitRequested += ms => { _timeline.Split(ms); UpdateLabels(); };
        _strip.SetKeptRequested += (ms, kept) => { _timeline.SetSegmentKept(ms, kept); UpdateLabels(); };
        _strip.RemoveSplitRequested += ms => { _timeline.RemoveSplitAt(ms); UpdateLabels(); };
        _strip.ZoomRequested += OnTimelineZoom;
        _strip.PanRequested += OnTimelinePan;

        _selectionBar = this.FindControl<Border>("SelectionBar");
        if (this.FindControl<Button>("CutSelectionButton") is { } cutBtn) cutBtn.Click += (_, _) => CutSelection();
        if (this.FindControl<Button>("KeepSelectionButton") is { } keepBtn) keepBtn.Click += (_, _) => KeepSelection();
        if (this.FindControl<Button>("KeepOnlySelectionButton") is { } keepOnlyBtn) keepOnlyBtn.Click += (_, _) => KeepOnlySelection();
        if (this.FindControl<Button>("ClearSelectionButton") is { } clrBtn) clrBtn.Click += (_, _) => DropSelection();
        // Any edit changes the kept ranges, so a running playback is now stale — stop and show a still.
        _timeline.Changed += () => { StopPlayback(showStill: true); _strip.Refresh(); UpdateLabels(); };
        // A cut/keep changes where the cursor is at each edited time — re-project the overlay to match.
        _timeline.Changed += ReprojectSmoothTrack;
        SeedTuningFromSettings();
        SetupSmoothingPanel();
        SetupEffectsLane();
        InitTimelineView();

        Closed += (_, _) => { ExitCanvasEdit(); PersistTuning(); PersistEdit(); _cts.Cancel(); _playTimer.Stop(); _player?.Dispose(); };

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

        // The authored edit document. A v2 doc carries the full effect track; a v1/fresh clip migrates its zoom
        // + cursor-shown flag and seeds the full-length defaults (cursor shown + ripples on) as editable blocks.
        var edit = ClipEdit.Load(Shrike.Core.AppStorage.EditDocFor(_source.Path));
        _effects.Clear();
        if (edit.HasEffectTrack)
        {
            _effects.AddRange(edit.Effects.Events);
        }
        else
        {
            _effects.AddRange(edit.ToEffectTrack(_timeline.DurationMs).Events); // zoom + full-length visibility
            if (_rippleDefaultOn && _timeline.DurationMs > 0) _effects.Add(new RippleEffect(0, _timeline.DurationMs));
        }
        _authoredZoom = ZoomTrackFromEffects();

        // Seed the lane from the loaded effects, and mark where clicks fired (snap targets).
        if (_effectsLane is not null)
        {
            _effectsLane.Timeline = _timeline;
            _effectsLane.Events = _effects;
            _effectsLane.ClickMarks = _smoothTrack?.Clicks.Where(c => c.Down).Select(c => (long)c.TMs).ToArray() ?? [];
            _effectsLane.Select(-1);
            _effectsLane.Refresh();
        }

        // The tuning panel + effects lane + properties pane only make sense for a clip that carries a track.
        // The pane stays visible for the whole session then (empty until a selection), so selecting an effect
        // never widens the window / reflows the editor.
        var hasTrack = _smoothTrack is not null;
        if (this.FindControl<Grid>("EffectsPanel") is { } effectsPanel) effectsPanel.IsVisible = hasTrack;
        if (_propsPane is not null) _propsPane.IsVisible = hasTrack;
        OnEffectSelectionChanged(-1); // seed the pane's empty state

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
        _propsPane = this.FindControl<Border>("PropsPane");
        _paneHeader = this.FindControl<TextBlock>("PaneHeader");
        _paneEmpty = this.FindControl<TextBlock>("PaneEmpty");
        _zoomEditor = this.FindControl<StackPanel>("ZoomEditor");
        _spotlightEditor = this.FindControl<StackPanel>("SpotlightEditor");
        _visibilityEditor = this.FindControl<StackPanel>("VisibilityEditor");
        _timingEditor = this.FindControl<Grid>("TimingEditor");
        _startInput = this.FindControl<NumericUpDown>("StartInput");
        _endInput = this.FindControl<NumericUpDown>("EndInput");
        _segmentEditor = this.FindControl<StackPanel>("SegmentEditor");
        _segStartInput = this.FindControl<NumericUpDown>("SegStartInput");
        _segEndInput = this.FindControl<NumericUpDown>("SegEndInput");
        _segKeepToggle = this.FindControl<CheckBox>("SegKeepToggle");
        _removeSplitButton = this.FindControl<Button>("RemoveSplitButton");
        _deleteButton = this.FindControl<Button>("DeleteZoomButton");
        _zoomAmountInput = this.FindControl<NumericUpDown>("ZoomAmountInput");
        _easeInInput = this.FindControl<NumericUpDown>("EaseInInput");
        _easeOutInput = this.FindControl<NumericUpDown>("EaseOutInput");
        _spotlightColorInput = this.FindControl<TextBox>("SpotlightColorInput");
        _spotlightOpacityInput = this.FindControl<Slider>("SpotlightOpacityInput");
        _spotlightRadiusInput = this.FindControl<NumericUpDown>("SpotlightRadiusInput");
        _visibilityInput = this.FindControl<CheckBox>("VisibilityInput");

        if (_effectsLane is not null)
        {
            _effectsLane.Timeline = _timeline;
            _effectsLane.Changed += OnEffectsChanged;
            _effectsLane.SelectionChanged += OnEffectSelectionChanged;
            _effectsLane.AddRequested += OnAddEffect;
            _effectsLane.DeleteRequested += DeleteEffectAt;
            _effectsLane.ZoomRequested += OnTimelineZoom;
            _effectsLane.PanRequested += OnTimelinePan;
        }
        _preview.TargetBoxDrawn += OnTargetBoxDrawn;
        if (this.FindControl<Button>("AddEffectButton") is { } add)
            add.Click += (_, _) => _effectsLane?.ShowAddMenu(add, atPointer: false, _playheadSourceMs, hitIndex: -1);
        if (this.FindControl<Button>("DeleteZoomButton") is { } del)
            del.Click += (_, _) => DeleteSelectedEffect();
        if (_zoomAmountInput is not null) _zoomAmountInput.ValueChanged += (_, _) => OnZoomPropsChanged();
        if (_easeInInput is not null) _easeInInput.ValueChanged += (_, _) => OnZoomPropsChanged();
        if (_easeOutInput is not null) _easeOutInput.ValueChanged += (_, _) => OnZoomPropsChanged();

        if (_spotlightColorInput is not null)
            _spotlightColorInput.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) OnSpotlightPropsChanged(); };
        if (_spotlightOpacityInput is not null)
            _spotlightOpacityInput.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) OnSpotlightPropsChanged(); };
        if (_spotlightRadiusInput is not null) _spotlightRadiusInput.ValueChanged += (_, _) => OnSpotlightPropsChanged();
        if (_visibilityInput is not null) _visibilityInput.IsCheckedChanged += (_, _) => OnVisibilityPropsChanged();
        if (_startInput is not null) _startInput.ValueChanged += (_, _) => OnTimingChanged();
        if (_endInput is not null) _endInput.ValueChanged += (_, _) => OnTimingChanged();
        if (_segStartInput is not null) _segStartInput.ValueChanged += (_, _) => OnSegStartChanged();
        if (_segEndInput is not null) _segEndInput.ValueChanged += (_, _) => OnSegEndChanged();
        if (_segKeepToggle is not null) _segKeepToggle.IsCheckedChanged += (_, _) => OnSegKeepChanged();
        if (_removeSplitButton is not null) _removeSplitButton.Click += (_, _) => OnRemoveSplit();

        _canvasSurface = this.FindControl<AnnotationSurface>("CanvasSurface");
        _canvasEditor = this.FindControl<StackPanel>("CanvasEditor");
        _canvasTools = this.FindControl<StackPanel>("CanvasTools");
        _canvasEditToggle = this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("CanvasEditToggle");
        _canvasScreenSpace = this.FindControl<CheckBox>("CanvasScreenSpace");
        _canvasAnimCombo = this.FindControl<ComboBox>("CanvasAnimCombo");
        if (_canvasEditToggle is not null) _canvasEditToggle.IsCheckedChanged += (_, _) => OnCanvasEditToggled();
        if (_canvasScreenSpace is not null) _canvasScreenSpace.IsCheckedChanged += (_, _) => OnCanvasSpaceChanged();
        if (_canvasAnimCombo is not null) _canvasAnimCombo.SelectionChanged += (_, _) => OnCanvasAnimChanged();
    }

    // The zoom effects, as the track the preview + export consume. Non-zoom effects don't affect framing.
    private ZoomTrack ZoomTrackFromEffects()
        => new(_effects.OfType<ZoomEffect>().Select(z => z.ToZoomEvent()).ToList());

    // The current authored effects as an immutable track — for per-frame preview lookups + export.
    private EffectTrack CurrentEffects => new(_effects);

    // The selected effect if it's a zoom (the only kind with an inspector + aim box today), else null.
    private ZoomEffect? SelectedZoomEffect()
        => _effectsLane is { SelectedIndex: var i } && i >= 0 && i < _effects.Count && _effects[i] is ZoomEffect z
            ? z : null;

    // The lane edited an effect's timing (drag/resize) — rebuild the zoom track and refresh the preview.
    private void OnEffectsChanged()
    {
        _authoredZoom = ZoomTrackFromEffects();
        RefreshTimingInputs();
        UpdateCursorOverlay();
    }

    // Mirror the selected effect's start/end into the pane inputs (e.g. after a lane drag), unless the user is
    // currently typing in them.
    private void RefreshTimingInputs()
    {
        if (_effectsLane is null) return;
        var i = _effectsLane.SelectedIndex;
        if (i < 0 || i >= _effects.Count) return;
        if (_startInput?.IsKeyboardFocusWithin == true || _endInput?.IsKeyboardFocusWithin == true) return;
        var ev = _effects[i];
        _suppressInspector = true;
        if (_startInput is not null) _startInput.Value = (decimal)(ev.StartMs / 1000.0);
        if (_endInput is not null) _endInput.Value = (decimal)(ev.EndMs / 1000.0);
        _suppressInspector = false;
    }

    private void OnEffectSelectionChanged(int index)
    {
        // Changing selection ends any in-progress canvas edit (commits it) before showing the new editor.
        if (_editingCanvasIndex >= 0) ExitCanvasEdit();

        var effect = index >= 0 && index < _effects.Count ? _effects[index] : null;
        var zoom = effect as ZoomEffect;
        var spot = effect as SpotlightEffect;
        var vis = effect as VisibilityEffect;
        var canvas = effect as CanvasEffect;

        // A segment and an effect are mutually exclusive selections in the pane.
        if (_segmentEditor is not null) _segmentEditor.IsVisible = false;
        if (effect is not null) _selSeg = null;

        // The pane is always present; only its content swaps to the selected effect's editor. Kinds without an
        // editor (ripple) show the empty-state note. Delete is offered for any selection.
        if (_paneHeader is not null) _paneHeader.Text = "✦ " + (effect is null ? "Effect" : KindName(effect.Kind));
        if (_zoomEditor is not null) _zoomEditor.IsVisible = zoom is not null;
        if (_spotlightEditor is not null) _spotlightEditor.IsVisible = spot is not null;
        if (_visibilityEditor is not null) _visibilityEditor.IsVisible = vis is not null;
        if (_canvasEditor is not null) _canvasEditor.IsVisible = canvas is not null;
        if (_timingEditor is not null) _timingEditor.IsVisible = effect is not null; // start/end for every kind
        if (_deleteButton is not null) _deleteButton.IsVisible = effect is not null;
        if (_paneEmpty is not null)
        {
            var hasEditor = zoom is not null || spot is not null || vis is not null || canvas is not null;
            _paneEmpty.IsVisible = !hasEditor;
            _paneEmpty.Text = effect is null
                ? "Select an effect on the timeline to edit it, or right-click the timeline to add one."
                : "No adjustable properties — drag to move / resize, or delete.";
        }

        // Aim box + inspector are zoom-only.
        _preview.AimMode = zoom is not null;
        _preview.SetTargetBox(zoom is not null ? EventBox(zoom) : null);

        _suppressInspector = true;
        if (effect is not null)
        {
            if (_startInput is not null) _startInput.Value = (decimal)(effect.StartMs / 1000.0);
            if (_endInput is not null) _endInput.Value = (decimal)(effect.EndMs / 1000.0);
        }
        if (zoom is not null)
        {
            if (_zoomAmountInput is not null) _zoomAmountInput.Value = (decimal)zoom.Zoom;
            if (_easeInInput is not null) _easeInInput.Value = (decimal)(zoom.EaseInMs / 1000.0);
            if (_easeOutInput is not null) _easeOutInput.Value = (decimal)(zoom.EaseOutMs / 1000.0);
        }
        else if (spot is not null)
        {
            if (_spotlightColorInput is not null) _spotlightColorInput.Text = spot.Color;
            if (_spotlightOpacityInput is not null) _spotlightOpacityInput.Value = spot.Opacity;
            if (_spotlightRadiusInput is not null) _spotlightRadiusInput.Value = spot.Radius;
        }
        else if (vis is not null)
        {
            if (_visibilityInput is not null) _visibilityInput.IsChecked = vis.Visible;
        }
        else if (canvas is not null)
        {
            if (_canvasScreenSpace is not null) _canvasScreenSpace.IsChecked = canvas.Space == CanvasSpace.Screen;
            // The combo applies presets; it can't reverse-map an arbitrary animation, so it shows the neutral
            // item on selection (picking a preset overwrites; leaving it keeps any existing keyframes).
            if (_canvasAnimCombo is not null) _canvasAnimCombo.SelectedIndex = 0;
            if (_canvasTools is not null) _canvasTools.IsVisible = false;
            if (_canvasEditToggle is not null) { _suppressCanvasToggle = true; _canvasEditToggle.IsChecked = false; _suppressCanvasToggle = false; }
        }
        _suppressInspector = false;

        UpdateCursorOverlay(); // aiming shows the full frame; deselect restores the zoom view
    }

    // ---- inline canvas editing ----

    private void OnCanvasToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_canvasSurface is not null && sender is Button { Tag: string tag }
            && Enum.TryParse<AnnotationTool>(tag, out var tool))
            _canvasSurface.Tool = tool;
    }

    private void OnCanvasColorClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_canvasSurface is not null && sender is Button { Tag: string hex }) _canvasSurface.StrokeColorHex = hex;
    }

    private void OnCanvasSpaceChanged()
    {
        if (_suppressInspector || _effectsLane is null) return;
        var i = _effectsLane.SelectedIndex;
        if (i < 0 || i >= _effects.Count || _effects[i] is not CanvasEffect ev) return;
        _effects[i] = ev with { Space = _canvasScreenSpace?.IsChecked == true ? CanvasSpace.Screen : CanvasSpace.Content };
        UpdateCursorOverlay();
    }

    // The animation preset dropdown changed — apply the chosen preset's keyframes to the selected canvas.
    private void OnCanvasAnimChanged()
    {
        if (_suppressInspector || _effectsLane is null) return;
        var i = _effectsLane.SelectedIndex;
        if (i < 0 || i >= _effects.Count || _effects[i] is not CanvasEffect ev) return;
        var kind = (_canvasAnimCombo?.SelectedIndex ?? 0) switch
        {
            1 => CanvasAnimationKind.Fade,
            2 => CanvasAnimationKind.SlideLeft,
            3 => CanvasAnimationKind.SlideRight,
            4 => CanvasAnimationKind.SlideUp,
            5 => CanvasAnimationKind.Pop,
            _ => CanvasAnimationKind.None,
        };
        var anim = kind == CanvasAnimationKind.None
            ? CanvasAnimation.Identity
            : CanvasAnimationPresets.Build(kind, ev.DurationMs, _source.Width, _source.Height);
        _effects[i] = ev with { Animation = anim };
        UpdateCursorOverlay();
    }

    private void OnCanvasEditToggled()
    {
        if (_suppressCanvasToggle) return;
        if (_canvasEditToggle?.IsChecked == true) _ = EnterCanvasEdit();
        else ExitCanvasEdit();
    }

    // Open the annotation surface over the preview, backed by the frame at the playhead (if inside the span)
    // or the span start, seeded with the effect's current drawing. Edits commit live to the effect.
    private async Task EnterCanvasEdit()
    {
        if (_canvasSurface is null || _effectsLane is null) return;
        var i = _effectsLane.SelectedIndex;
        if (i < 0 || i >= _effects.Count || _effects[i] is not CanvasEffect c) return;

        StopPlayback();
        var srcMs = _playheadSourceMs >= c.StartMs && _playheadSourceMs <= c.EndMs ? _playheadSourceMs : c.StartMs;
        byte[]? png;
        try { png = await Task.Run(() => _extractor.ExtractPng(srcMs), _cts.Token).ConfigureAwait(true); }
        catch { png = null; }
        if (png is null || _effectsLane.SelectedIndex != i || _effects[i] is not CanvasEffect cc)
        {
            if (_canvasEditToggle is not null) { _suppressCanvasToggle = true; _canvasEditToggle.IsChecked = false; _suppressCanvasToggle = false; }
            return;
        }

        CapturedImage frame;
        try { frame = ImageCodec.DecodeToCaptured(png); }
        catch { if (_canvasEditToggle is not null) { _suppressCanvasToggle = true; _canvasEditToggle.IsChecked = false; _suppressCanvasToggle = false; } return; }

        _editingCanvasIndex = i;
        _canvasDoc = new AnnotationDocument();
        foreach (var a in cc.Annotations) _canvasDoc.Add(a);
        _canvasDoc.Changed += OnCanvasDocChanged;
        _canvasSurface.SetContent(frame, _canvasDoc);
        _canvasSurface.Tool = AnnotationTool.None;
        _canvasSurface.IsVisible = true;
        _preview.IsVisible = false;
        if (_canvasTools is not null) _canvasTools.IsVisible = true;
    }

    private void ExitCanvasEdit()
    {
        if (_editingCanvasIndex < 0) return;
        if (_canvasDoc is not null) { _canvasDoc.Changed -= OnCanvasDocChanged; CommitCanvas(); }
        _canvasDoc = null;
        _editingCanvasIndex = -1;
        if (_canvasSurface is not null) _canvasSurface.IsVisible = false;
        _preview.IsVisible = true;
        if (_canvasTools is not null) _canvasTools.IsVisible = false;
        if (_canvasEditToggle is not null && _canvasEditToggle.IsChecked == true)
        { _suppressCanvasToggle = true; _canvasEditToggle.IsChecked = false; _suppressCanvasToggle = false; }
        UpdateCursorOverlay();
    }

    private void OnCanvasDocChanged() => CommitCanvas();

    // Write the live document back to the effect, keeping the drawing in source-frame pixels.
    private void CommitCanvas()
    {
        if (_editingCanvasIndex < 0 || _canvasDoc is null) return;
        if (_editingCanvasIndex >= _effects.Count || _effects[_editingCanvasIndex] is not CanvasEffect c) return;
        _canvasLayerCache.Remove(c.Annotations); // the old layer bitmap is now stale
        _effects[_editingCanvasIndex] = c with { Annotations = _canvasDoc.Items.ToList() };
    }

    // The rendered (cached) layer bitmap for a canvas effect at source resolution, for the preview overlay.
    private Bitmap? CanvasLayerBitmap(CanvasEffect c)
    {
        if (c.Annotations.Count == 0 || _source.Width <= 0 || _source.Height <= 0) return null;
        if (_canvasLayerCache.TryGetValue(c.Annotations, out var cached)) return cached;

        var surface = new AnnotationSurface();
        var doc = new AnnotationDocument();
        foreach (var a in c.Annotations) doc.Add(a);
        var blank = new CapturedImage(_source.Width, _source.Height, new byte[_source.Width * _source.Height * 4],
            new PixelBounds(0, 0, _source.Width, _source.Height), DateTimeOffset.Now);
        surface.SetContent(blank, doc);
        var bytes = surface.RenderAnnotationLayer(_source.Width, _source.Height);
        if (bytes is null) return null;

        var wb = new WriteableBitmap(new PixelSize(_source.Width, _source.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = wb.Lock())
        {
            var rowBytes = _source.Width * 4;
            for (var row = 0; row < _source.Height; row++)
                Marshal.Copy(bytes, row * rowBytes, fb.Address + row * fb.RowBytes, rowBytes);
        }
        _canvasLayerCache[c.Annotations] = wb;
        return wb;
    }

    // The spotlight editor changed — apply colour / opacity / radius to the selected spotlight.
    private void OnSpotlightPropsChanged()
    {
        if (_suppressInspector || _effectsLane is null) return;
        var i = _effectsLane.SelectedIndex;
        if (i < 0 || i >= _effects.Count || _effects[i] is not SpotlightEffect ev) return;
        var color = string.IsNullOrWhiteSpace(_spotlightColorInput?.Text) ? ev.Color : _spotlightColorInput!.Text!.Trim();
        _effects[i] = ev with
        {
            Color = color,
            Opacity = Math.Clamp(_spotlightOpacityInput?.Value ?? ev.Opacity, 0.1, 1.0),
            Radius = (int)Math.Clamp((double)(_spotlightRadiusInput?.Value ?? ev.Radius), 12, 160),
        };
        UpdateCursorOverlay();
    }

    // The Start/End inputs changed — retime the selected effect (any kind), clamped to the clip + a floor.
    private void OnTimingChanged()
    {
        if (_suppressInspector || _effectsLane is null) return;
        var i = _effectsLane.SelectedIndex;
        if (i < 0 || i >= _effects.Count) return;
        const long minDur = 200;
        var dur = _timeline.DurationMs;
        var start = (long)Math.Round((double)(_startInput?.Value ?? 0) * 1000);
        var end = (long)Math.Round((double)(_endInput?.Value ?? 0) * 1000);
        start = Math.Clamp(start, 0, Math.Max(0, dur - minDur));
        end = Math.Clamp(end, start + minDur, dur);
        _effects[i] = _effects[i] with { StartMs = start, EndMs = end };
        OnEffectsChanged();
        _effectsLane.Refresh();
    }

    // The visibility editor changed — flip the selected span between shown/hidden.
    private void OnVisibilityPropsChanged()
    {
        if (_suppressInspector || _effectsLane is null) return;
        var i = _effectsLane.SelectedIndex;
        if (i < 0 || i >= _effects.Count || _effects[i] is not VisibilityEffect ev) return;
        _effects[i] = ev with { Visible = _visibilityInput?.IsChecked ?? true };
        _effectsLane.Refresh(); // the block label shows shown/hidden
        UpdateCursorOverlay();
    }

    private static string KindName(EffectKind kind) => kind switch
    {
        EffectKind.Zoom => "Zoom",
        EffectKind.Spotlight => "Spotlight",
        EffectKind.Ripple => "Click ripple",
        EffectKind.Visibility => "Mouse visibility",
        EffectKind.Canvas => "Canvas",
        _ => "Effect",
    };

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

        _suppressInspector = true;
        if (_zoomAmountInput is not null) _zoomAmountInput.Value = (decimal)zoom;
        _suppressInspector = false;
        _preview.SetTargetBox(EventBox((ZoomEffect)_effects[i])); // snap the shown box to the aspect-correct square
        OnEffectsChanged();
        _effectsLane.Refresh();
    }

    // The right-pane inputs changed — apply zoom + independent ease-in/out to the selected zoom effect.
    private void OnZoomPropsChanged()
    {
        if (_suppressInspector || _effectsLane is null) return;
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
            EffectKind.Canvas => new CanvasEffect(start, end, 0, 0, CanvasSpace.Content), // hard cut so redaction stays opaque
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
            _preview.SetCursor(null); _preview.SetViewport(null); _preview.SetRipples([]); _preview.SetSpotlight(null);
            _preview.SetCanvasLayers(CanvasLayersAt(_timeline.EditedToSourceMs(_currentEditedMs)));
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

        // Visibility / ripple / spotlight are resolved from the effect track at this frame's source time — the
        // same lookups the export uses — so the preview mirrors the file. Zoom still applies when the cursor is
        // hidden; only the cursor + ripples drop out.
        var srcMs = _timeline.EditedToSourceMs((long)(i * 1000.0 / _smoothed.Fps));
        var fx = CurrentEffects;
        var visible = fx.VisibilityAt(srcMs)?.Visible ?? true;
        var ripplesOn = fx.RipplesEnabledAt(srcMs);
        _preview.SetCursor(visible ? Norm(s.X, s.Y) : null);
        _preview.SetRipples(visible && ripplesOn ? ActiveRipples(i, vp) : []);

        var sf = fx.SpotlightAt(srcMs, _source.Height);
        _preview.SetSpotlight(sf.Active
            ? new PreviewSurface.PreviewSpotlight(Norm(s.X, s.Y), sf.RadiusPx / _source.Height,
                Avalonia.Media.Color.FromRgb(sf.R, sf.G, sf.B), sf.Alpha)
            : null);

        _preview.SetCanvasLayers(CanvasLayersAt(srcMs));
    }

    // The canvas layers active at a source time, as preview overlays (skipped while inline-editing, since the
    // annotation surface itself is showing the live drawing).
    private IReadOnlyList<PreviewSurface.PreviewCanvas> CanvasLayersAt(long srcMs)
    {
        if (_editingCanvasIndex >= 0) return [];
        var layers = new List<PreviewSurface.PreviewCanvas>();
        foreach (var c in _effects.OfType<CanvasEffect>())
            if (c.ActiveAt(srcMs) && CanvasLayerBitmap(c) is { } bmp)
            {
                var local = Math.Clamp(srcMs - c.StartMs, 0, c.DurationMs);
                var t = c.Animation.SampleAt(local);
                t = t with { Opacity = c.RampAt(srcMs) * t.Opacity };
                layers.Add(new PreviewSurface.PreviewCanvas(bmp, c.Space == CanvasSpace.Content, t));
            }
        return layers;
    }

    /// <summary>The click ripples live at frame <paramref name="i"/>, mirrored from the export's cursor compositor
    /// (same lifetime, radii, and viewport mapping) so the preview matches the file.</summary>
    private IReadOnlyList<PreviewSurface.PreviewRipple> ActiveRipples(int i, ZoomViewport vp)
    {
        if (_smoothed is null || _smoothed.IsEmpty || _source.Height <= 0)
            return [];

        var style = CursorStyle.ForExport(_source.Height, _cursorSize);
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
        try { new ClipEdit(CurrentEffects).Save(Shrike.Core.AppStorage.EditDocFor(_source.Path)); }
        catch { /* best effort — never block closing on a failed save */ }
    }

    /// <summary>Start the editor from the persisted tuning so a dialled-in look carries across sessions.</summary>
    private void SeedTuningFromSettings()
    {
        var s = Services.SettingsService.Instance?.Current ?? Shrike.Core.Settings.AppSettings.Default;
        _smoothing = CursorSmoothing.FromSmoothness(s.CursorSmoothness);
        _cursorSize = s.CursorSize;
        _rippleDefaultOn = s.CursorRippleEnabled; // the seed for a new/migrated clip's full-length ripple block
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
        _sizeSlider = this.FindControl<Slider>("SizeSlider");
        _sizeValue = this.FindControl<TextBlock>("SizeValue");
        if (_sizeSlider is not null)
        {
            _sizeSlider.Value = _cursorSize;
            _sizeSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) OnCursorSizeChanged();
            };
        }
        if (this.FindControl<Button>("SmoothingReset") is { } reset)
            reset.Click += (_, _) => ResetSmoothing();

        UpdateSmoothingLabels();
        UpdateCursorScale();
    }

    private void OnCursorSizeChanged()
    {
        _cursorSize = Math.Clamp(_sizeSlider?.Value ?? _cursorSize, 0.5, 2.0);
        UpdateSmoothingLabels();
        UpdateCursorScale();
        UpdateCursorOverlay(); // refresh cursor/ripple size at the current frame
    }

    /// <summary>Keep the previewed cursor the same relative size as the export renders it (WYSIWYG).</summary>
    private void UpdateCursorScale()
    {
        if (_source.Height <= 0) return;
        var frac = CursorStyle.ForExport(_source.Height, _cursorSize).Height / (double)_source.Height;
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
        if (_smoothnessSlider is not null) _smoothnessSlider.Value = _smoothing.Smoothness * 100.0;
        if (_sizeSlider is not null) _sizeSlider.Value = _cursorSize;
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
        // Move every playhead together — the source may be the strip OR the ruler, so drive all three (the
        // strip updates its own during its drag, but the ruler drag needs the strip synced, and vice-versa).
        _strip.SetPlayhead(sourceMs);
        _effectsLane?.SetPlayhead(sourceMs);
        _ruler?.SetPlayhead(sourceMs);
        RequestPreview(sourceMs);
        UpdateLabels();
        UpdateCursorOverlay();
    }

    private void OnSeek(long sourceMs) => OnScrub(sourceMs);

    // ---- timeline zoom / pan (shared view window across the ruler, strip and effects lane) ----

    private void InitTimelineView()
    {
        _viewStartMs = 0;
        _viewEndMs = _timeline.DurationMs;
        PushTimelineView();
    }

    private void PushTimelineView()
    {
        _ruler?.SetView(_viewStartMs, _viewEndMs);
        _strip.SetView(_viewStartMs, _viewEndMs);
        _effectsLane?.SetView(_viewStartMs, _viewEndMs);
    }

    // Ctrl+wheel: zoom the timeline around the pointer (wheel up = zoom in), keeping the pivot ms under the cursor.
    private void OnTimelineZoom(long pivotMs, double deltaY)
    {
        var dur = _timeline.DurationMs;
        if (dur <= 0) return;
        var span = _viewEndMs > _viewStartMs ? (double)(_viewEndMs - _viewStartMs) : dur;
        var minSpan = Math.Min(dur, 300.0);
        var newSpan = Math.Clamp(span * (deltaY > 0 ? 1.0 / 1.2 : 1.2), minSpan, dur);
        var frac = span > 0 ? (pivotMs - _viewStartMs) / span : 0.5;
        var newStart = (long)Math.Round(pivotMs - frac * newSpan);
        newStart = Math.Clamp(newStart, 0, Math.Max(0, dur - (long)Math.Round(newSpan)));
        _viewStartMs = newStart;
        _viewEndMs = Math.Min(dur, (long)Math.Round(newStart + newSpan));
        PushTimelineView();
    }

    // Shift+wheel: pan the view horizontally (no-op when fully zoomed out).
    private void OnTimelinePan(double deltaY)
    {
        var dur = _timeline.DurationMs;
        var span = _viewEndMs - _viewStartMs;
        if (span <= 0 || span >= dur) return;
        var newStart = Math.Clamp(_viewStartMs + (long)Math.Round(-deltaY * span * 0.15), 0, dur - span);
        _viewStartMs = newStart;
        _viewEndMs = newStart + span;
        PushTimelineView();
    }

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

        // While inline-editing a canvas, keys drive the drawing: Delete removes the selected annotation, Esc
        // finishes. Timeline shortcuts (space / effect delete-nudge) are suspended so they don't interfere.
        if (_editingCanvasIndex >= 0)
        {
            if (e.Key is Avalonia.Input.Key.Delete or Avalonia.Input.Key.Back) { _canvasSurface?.DeleteSelected(); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Escape) { ExitCanvasEdit(); e.Handled = true; }
            return;
        }

        // Space toggles play/pause.
        if (e.Key == Avalonia.Input.Key.Space)
        {
            TogglePlayPause();
            e.Handled = true;
            return;
        }

        // A drag-selection on the strip: Esc clears it, Delete cuts it (when no effect is selected).
        if (_strip.Selection is not null && (_effectsLane?.SelectedIndex ?? -1) < 0)
        {
            if (e.Key == Avalonia.Input.Key.Escape) { DropSelection(); e.Handled = true; return; }
            if (e.Key is Avalonia.Input.Key.Delete or Avalonia.Input.Key.Back) { CutSelection(); e.Handled = true; return; }
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
        _ruler?.SetPlayhead(_playheadSourceMs);
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

    // ---- editing (direct trim: handles + drag-select) ----

    private const long MinKeptMs = 200;

    // Dragging the head trim-handle: set where the kept region starts, preserving interior cuts.
    private void OnTrimHead(long ms)
    {
        var kr = _timeline.KeptRanges;
        if (kr.Count == 0) return;
        long start = kr[0].StartMs, end = kr[^1].EndMs;
        ms = Math.Clamp(ms, 0, end - MinKeptMs);
        if (ms > start) _timeline.Cut(start, ms);
        else if (ms < start) _timeline.Keep(ms, start);
    }

    private void OnTrimTail(long ms)
    {
        var kr = _timeline.KeptRanges;
        if (kr.Count == 0) return;
        long start = kr[0].StartMs, end = kr[^1].EndMs;
        ms = Math.Clamp(ms, start + MinKeptMs, _timeline.DurationMs);
        if (ms < end) _timeline.Cut(ms, end);
        else if (ms > end) _timeline.Keep(end, ms);
    }

    // A range was drag-selected on the strip — position + show the floating Cut / Keep bar just ABOVE the strip
    // (so it doesn't cover the selection you're making), centred over the selection.
    private void OnRangeSelected(long a, long b)
    {
        if (_selectionBar is null) return;
        _selectionBar.IsVisible = true;
        var mid = (_strip.MsToX(a) + _strip.MsToX(b)) / 2.0;
        var barW = _selectionBar.Bounds.Width > 0 ? _selectionBar.Bounds.Width : 200;
        var barH = _selectionBar.Bounds.Height > 0 ? _selectionBar.Bounds.Height : 30;
        _selectionBar.Margin = new Thickness(Math.Clamp(mid - barW / 2, 0, Math.Max(0, _strip.Bounds.Width - barW)), -(barH + 4), 0, 0);
    }

    private void OnSelectionCleared()
    {
        if (_selectionBar is not null) _selectionBar.IsVisible = false;
    }

    private void CutSelection()
    {
        if (_strip.Selection is { } sel) _timeline.Cut(sel.A, sel.B);
        DropSelection();
    }

    // Keep: mark the selection kept (restore it), leaving the rest as-is.
    private void KeepSelection()
    {
        if (_strip.Selection is { } sel) _timeline.Keep(sel.A, sel.B);
        DropSelection();
    }

    // Keep only: keep the selection and cut everything else.
    private void KeepOnlySelection()
    {
        if (_strip.Selection is { } sel) _timeline.KeepOnly(sel.A, sel.B);
        DropSelection();
    }

    private void DropSelection()
    {
        _strip.ClearSelection();
        OnSelectionCleared();
        UpdateLabels();
    }

    private void OnResetAll(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _timeline.RestoreAll();
        DropSelection();
        ClearSegmentSelection();
        OnEffectSelectionChanged(_effectsLane?.SelectedIndex ?? -1);
    }

    // ---- segment (strip span) editing in the pane ----

    private long SplitTolMs() => Math.Max(1, _timeline.DurationMs / 200);

    private void OnSegmentSelected(long start, long end)
    {
        _selSeg = (start, end);
        _effectsLane?.Select(-1);   // a segment and an effect can't both be "the selection" → drop the effect
        ShowSegmentEditor();
    }

    private void ShowSegmentEditor()
    {
        if (_selSeg is not { } s || _segmentEditor is null) return;
        if (_paneHeader is not null) _paneHeader.Text = "✦ Segment";
        if (_paneEmpty is not null) _paneEmpty.IsVisible = false;
        if (_timingEditor is not null) _timingEditor.IsVisible = false;
        if (_deleteButton is not null) _deleteButton.IsVisible = false;
        _segmentEditor.IsVisible = true;

        var seg = _timeline.Find((s.Start + s.End) / 2);
        _suppressInspector = true;
        if (_segStartInput is not null) _segStartInput.Value = (decimal)(s.Start / 1000.0);
        if (_segEndInput is not null) _segEndInput.Value = (decimal)(s.End / 1000.0);
        if (_segKeepToggle is not null) _segKeepToggle.IsChecked = seg?.Kept ?? true;
        _suppressInspector = false;
        if (_removeSplitButton is not null)
            _removeSplitButton.IsEnabled = _timeline.HasSplitNear(s.Start, SplitTolMs()) || _timeline.HasSplitNear(s.End, SplitTolMs());
    }

    private void ClearSegmentSelection()
    {
        _selSeg = null;
        if (_segmentEditor is not null) _segmentEditor.IsVisible = false;
    }

    private void OnSegStartChanged()
    {
        if (_suppressInspector || _selSeg is not { } s) return;
        var v = Math.Clamp((long)Math.Round((double)(_segStartInput?.Value ?? 0) * 1000), 0, s.End - 100);
        if (s.Start == 0) { if (v > 0) _timeline.Cut(0, v); }   // first span → trimming its start cuts the head
        else _timeline.MoveBoundary(s.Start, v);
        _selSeg = (v, s.End);
        UpdateLabels();
    }

    private void OnSegEndChanged()
    {
        if (_suppressInspector || _selSeg is not { } s) return;
        var dur = _timeline.DurationMs;
        var v = Math.Clamp((long)Math.Round((double)(_segEndInput?.Value ?? 0) * 1000), s.Start + 100, dur);
        if (s.End == dur) { if (v < dur) _timeline.Cut(v, dur); }  // last span → trimming its end cuts the tail
        else _timeline.MoveBoundary(s.End, v);
        _selSeg = (s.Start, v);
        UpdateLabels();
    }

    private void OnSegKeepChanged()
    {
        if (_suppressInspector || _selSeg is not { } s) return;
        _timeline.SetSegmentKept((s.Start + s.End) / 2, _segKeepToggle?.IsChecked == true);
        UpdateLabels();
    }

    private void OnRemoveSplit()
    {
        if (_selSeg is not { } s) return;
        if (_timeline.HasSplitNear(s.Start, SplitTolMs())) _timeline.RemoveSplitAt(s.Start);
        else if (_timeline.HasSplitNear(s.End, SplitTolMs())) _timeline.RemoveSplitAt(s.End);
        UpdateLabels();
        ClearSegmentSelection();
        OnEffectSelectionChanged(-1);
    }

    // ---- export ----

    private async void OnExport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        StopPlayback();
        if (!_timeline.HasKeptContent) return;
        PersistEdit(); // make sure the authored zoom is on disk before we (and the export) read it
        var dlg = new ExportDialog(_source, _timeline, _ffmpegPath);
        // Carry the tuned smoothing/size + the authored effect track into the export so the file matches the preview.
        dlg.ConfigureEffects(_smoothing, _cursorSize, CurrentEffects);
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
