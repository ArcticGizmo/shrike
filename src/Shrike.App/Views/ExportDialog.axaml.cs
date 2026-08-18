using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Shrike.App.Native;
using Shrike.App.Services;
using Shrike.Core.Capture;
using Shrike.Core.Recording;
using static Shrike.Core.Recording.HardwareEncoders;

namespace Shrike.App.Views;

/// <summary>
/// The export half of the timeline editor: pick a preset, see the target spec and an estimated size, then
/// Save (file picker) or Copy-as-file (CF_HDROP, for pasting straight into Slack). Runs the encode off the
/// UI thread via <see cref="VideoExporter"/> with a live progress bar, and — since a hardware encoder can
/// be flaky — transparently retries on software if the hardware path fails.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class ExportDialog : Window
{
    private readonly RecordingSource _source;
    private readonly Timeline _timeline;
    private readonly string _ffmpegPath;
    private CancellationTokenSource? _exportCts;
    private bool _running;

    private ComboBox _presetBox = null!;
    private TextBlock _noteText = null!, _specText = null!, _sizeText = null!, _lengthText = null!, _statusText = null!;
    private StackPanel _progressPanel = null!;
    private ProgressBar _bar = null!;
    private Button _saveButton = null!, _copyButton = null!, _cancelButton = null!;

    public ExportDialog() : this(new RecordingSource("", 16, 16, 30, TimeSpan.FromSeconds(1)), new Timeline(1000), "") { }

    internal ExportDialog(RecordingSource source, Timeline timeline, string ffmpegPath)
    {
        _source = source;
        _timeline = timeline;
        _ffmpegPath = ffmpegPath;
        InitializeComponent();

        _presetBox = this.FindControl<ComboBox>("PresetBox")!;
        _noteText = this.FindControl<TextBlock>("NoteText")!;
        _specText = this.FindControl<TextBlock>("SpecText")!;
        _sizeText = this.FindControl<TextBlock>("SizeText")!;
        _lengthText = this.FindControl<TextBlock>("LengthText")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _progressPanel = this.FindControl<StackPanel>("ProgressPanel")!;
        _bar = this.FindControl<ProgressBar>("Bar")!;
        _saveButton = this.FindControl<Button>("SaveButton")!;
        _copyButton = this.FindControl<Button>("CopyButton")!;
        _cancelButton = this.FindControl<Button>("CancelButton")!;

        _presetBox.ItemsSource = ExportProfile.Presets.Select(p => p.Name).ToList();
        _presetBox.SelectedIndex = 0;
        _lengthText.Text = Fmt(_timeline.KeptDurationMs);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private ExportProfile Selected => ExportProfile.Presets[Math.Max(0, _presetBox.SelectedIndex)];

    private void OnPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_timeline.KeptRanges.Count == 0) return;
        var profile = Selected;
        _noteText.Text = profile.Note;

        // Build once (dummy path) to learn the real target dims/fps, then estimate.
        var cmd = ExportCommand.Build(_source, _timeline.KeptRanges, profile, hardware: null, "preview" + profile.Extension);
        _specText.Text = Spec(profile, cmd);

        long? sourceBytes = TryFileLength(_source.Path);
        var est = ExportSize.EstimateBytes(profile, cmd.TargetWidth, cmd.TargetHeight, cmd.TargetFps,
            _timeline.KeptDurationMs, sourceBytes, _source.DurationMs);
        _sizeText.Text = est is { } b ? "~" + HumanBytes(b) : "—";
    }

    private static string Spec(ExportProfile p, ExportCommand cmd)
    {
        var codec = p.Codec switch
        {
            ExportCodec.H264 => "H.264",
            ExportCodec.H265 => "H.265/HEVC",
            ExportCodec.Copy => cmd.IsReencode ? "Source (re-encoded)" : "Source (copy)",
            ExportCodec.Gif => "GIF",
            ExportCodec.WebP => "WebP",
            _ => p.Codec.ToString(),
        };
        if (p.Codec == ExportCodec.Copy)
            return $"{codec} · {cmd.TargetHeight}p · {cmd.TargetFps} fps";
        var quality = p.Codec is ExportCodec.H264 or ExportCodec.H265 ? $" · CRF {p.Crf}" : "";
        return $"{codec} · {cmd.TargetHeight}p · {cmd.TargetFps} fps{quality}";
    }

    // ---- output ----

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_running) return;
        var profile = Selected;

        IStorageFolder? start = null;
        if (SettingsService.Instance?.Current.DefaultSaveDirectory is { } dir && Directory.Exists(dir))
            start = await StorageProvider.TryGetFolderFromPathAsync(dir);

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export recording",
            SuggestedFileName = CaptureNaming.Expand(CaptureNaming.DefaultTemplate, DateTimeOffset.Now),
            DefaultExtension = profile.Extension.TrimStart('.'),
            SuggestedStartLocation = start,
            FileTypeChoices = new[]
            {
                new FilePickerFileType(profile.Codec.ToString())
                {
                    Patterns = new[] { "*" + profile.Extension },
                },
            },
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        await RunExport(path, copyToClipboard: false);
    }

    private async void OnCopyFile(object? sender, RoutedEventArgs e)
    {
        if (_running) return;
        // Export to a stable temp file, then put it on the clipboard for paste into Slack/Explorer.
        var path = Path.Combine(Path.GetTempPath(),
            CaptureNaming.Expand(CaptureNaming.DefaultTemplate, DateTimeOffset.Now) + Selected.Extension);
        await RunExport(path, copyToClipboard: true);
    }

    private async Task RunExport(string outputPath, bool copyToClipboard)
    {
        var profile = Selected;
        var hw = HardwareEncoders.Best(profile.Codec, _ffmpegPath);

        SetRunning(true, "Exporting…");
        var progress = new Progress<double>(v => _bar.Value = v);
        _exportCts = new CancellationTokenSource();

        try
        {
            await Encode(profile, hw, outputPath, progress, _exportCts.Token);
        }
        catch (OperationCanceledException)
        {
            SetRunning(false, "Cancelled.");
            return;
        }
        catch (Exception) when (hw is not null)
        {
            // Hardware encoder failed — fall back to software rather than leaving the user stuck.
            _statusText.Text = "Hardware encoder failed — retrying on software…";
            _bar.Value = 0;
            try
            {
                await Encode(profile, hardware: null, outputPath, progress, _exportCts.Token);
            }
            catch (Exception ex2)
            {
                Fail(ex2);
                return;
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
            return;
        }

        Finish(outputPath, copyToClipboard);
    }

    private CursorSmoothing _smoothing = CursorSmoothing.Default;
    private EffectTrack _effects = EffectTrack.Empty;
    private ZoomTrack _authoredZoom = ZoomTrack.Empty;
    private double _cursorSize = 1.0;

    /// <summary>Carry the editor's tuned smoothing + the authored effect track into the export so what you saw
    /// in the preview is what gets rendered. Zoom is a frame transform; the cursor / ripple / spotlight / mouse
    /// -visibility effects are resolved per frame from the track (an empty track means a plain transcode).</summary>
    internal void ConfigureEffects(CursorSmoothing smoothing, double cursorSize, EffectTrack effects)
    {
        _smoothing = smoothing;
        _cursorSize = cursorSize;
        _effects = effects;
        _authoredZoom = effects.ZoomTrack;
    }

    private async Task Encode(ExportProfile profile, HwEncoder? hardware, string outputPath,
        IProgress<double> progress, CancellationToken ct)
    {
        // Composite in post when there's something to draw. The cursor overlay needs the logged pointer track;
        // authored zoom is a pure frame transform that needs no track, so it composites on its own too.
        var track = LoadTrack();
        // Cursor / ripple / spotlight all need the pointer track; visibility gates the cursor per frame at
        // composite time (so a whole-clip hide simply draws nothing). Zoom + canvas are pure frame effects.
        var wantsCursor = track is { Points.Count: > 0 };
        var wantsZoom = !_authoredZoom.IsEmpty;
        var wantsCanvas = _effects.OfKind<CanvasEffect>().Any(c => c.Annotations.Count > 0);
        if (wantsCursor || wantsZoom || wantsCanvas)
        {
            await CompositeExport(profile, hardware, wantsCursor ? track : null, outputPath, progress, ct);
            return;
        }

        var cmd = ExportCommand.Build(_source, _timeline.KeptRanges, profile, hardware, outputPath);
        await new VideoExporter(_ffmpegPath).ExportAsync(cmd, _timeline.KeptDurationMs, progress, ct);
    }

    /// <summary>Rasterise one canvas effect's annotations to a premultiplied-BGRA layer sprite at the export
    /// size, reusing the annotation editor's own renderer (so it matches the preview). Runs on the UI thread.
    /// Annotations are in source-frame pixels, so a blank source-sized base sets the scale to the export size.</summary>
    private byte[]? RasterizeCanvas(CanvasEffect c, int w, int h)
    {
        var surface = new Controls.AnnotationSurface();
        var doc = new Shrike.Core.Annotations.AnnotationDocument();
        foreach (var a in c.Annotations) doc.Add(a);
        var blank = new CapturedImage(_source.Width, _source.Height,
            new byte[_source.Width * _source.Height * 4],
            new PixelBounds(0, 0, _source.Width, _source.Height), System.DateTimeOffset.Now);
        surface.SetContent(blank, doc);
        return surface.RenderAnnotationLayer(w, h);
    }

    private MouseTrack? LoadTrack()
    {
        var sidecar = Shrike.Core.AppStorage.SidecarFor(_source.Path);
        if (!File.Exists(sidecar)) return null;
        try { return MouseTrack.Load(sidecar); }
        catch { return null; }
    }

    /// <summary>
    /// Two stages, for full preset parity: (1) composite the effect chain (authored zoom transform + the
    /// smoothed cursor overlay) into a high-quality intermediate at the target size, then (2) run the normal
    /// export on that intermediate, so every preset (H.265 / hardware / GIF / WebP / Source) ships the effects
    /// baked in. <paramref name="track"/> is null when the cursor isn't drawn (zoom-only) — zoom needs no track.
    /// </summary>
    private async Task CompositeExport(ExportProfile profile, HwEncoder? hardware, MouseTrack? track,
        string outputPath, IProgress<double> progress, CancellationToken ct)
    {
        var probe = ExportCommand.Build(_source, _timeline.KeptRanges, profile, hardware: null, outputPath);
        int w = probe.TargetWidth, h = probe.TargetHeight, fps = probe.TargetFps;

        // The cursor overlay (when drawn) supplies the per-output-frame positions; zoom just needs the frame
        // count, which we derive from the kept duration when there's no cursor.
        var smoothed = track is not null ? SmoothCursor.Project(track, _timeline, fps, w, h, _smoothing) : null;
        var frameCount = smoothed is not null && !smoothed.IsEmpty
            ? smoothed.Frames.Count
            : (int)Math.Max(1, Math.Round(_timeline.KeptDurationMs / 1000.0 * fps));

        // Compose the effect chain in order: the zoom transform first (only with authored events), then the
        // spotlight glow (under the cursor), then the cursor + ripple overlay — each an IFrameCompositor.
        // Visibility / ripple are per-frame masks resolved from the effect track; spotlight is per-frame too.
        ZoomViewport[]? viewports = _authoredZoom.IsEmpty ? null : _authoredZoom.Resolve(_timeline, frameCount, fps, w, h);

        // Canvas layers rasterise to sprites here on the UI thread (Avalonia render), split by space: content
        // canvases go under the zoom transform (they magnify with it); screen canvases sit on top of everything.
        var contentCanvas = new List<IFrameCompositor>();
        var screenCanvas = new List<IFrameCompositor>();
        foreach (var c in _effects.OfKind<CanvasEffect>().Where(c => c.Annotations.Count > 0))
        {
            var sprite = RasterizeCanvas(c, w, h);
            if (sprite is null) continue;
            var comp = new CanvasCompositor(sprite, w, h, EffectTrack.ResolveEnvelope(c, _timeline, frameCount, fps));
            (c.Space == CanvasSpace.Screen ? screenCanvas : contentCanvas).Add(comp);
        }

        var effects = new List<IFrameCompositor>();
        effects.AddRange(contentCanvas);                                   // content canvas — under the zoom
        if (viewports is not null) effects.Add(new ZoomCompositor(viewports));
        if (smoothed is not null && !smoothed.IsEmpty)
        {
            if (_effects.HasSpotlight)
                effects.Add(new SpotlightCompositor(smoothed, _effects.ResolveSpotlight(_timeline, frameCount, fps, h), viewports));
            effects.Add(new CursorCompositor(smoothed, CursorStyle.ForExport(h, _cursorSize),
                viewports,
                _effects.ResolveCursorVisible(_timeline, frameCount, fps),
                _effects.ResolveRipplesEnabled(_timeline, frameCount, fps)));
        }
        effects.AddRange(screenCanvas);                                    // screen canvas — fixed, on top
        if (effects.Count == 0)
        {
            var plain = ExportCommand.Build(_source, _timeline.KeptRanges, profile, hardware, outputPath);
            await new VideoExporter(_ffmpegPath).ExportAsync(plain, _timeline.KeptDurationMs, progress, ct);
            return;
        }
        var compositor = new CompositorChain(effects);

        var intermediate = Path.Combine(Path.GetTempPath(), "shrike-cursor-" + Guid.NewGuid().ToString("N") + ".mp4");
        var interBitrate = (int)Math.Clamp((long)w * h * fps / 3, 8_000_000, 80_000_000); // generous → near-transparent
        try
        {
            // Stage 1 — composite to the intermediate (0..50% of the bar).
            var stage1 = new Progress<double>(v => progress.Report(v * 0.5));
            var pipeline = new FrameCompositePipeline(_ffmpegPath);
            await Task.Run(() => pipeline.Run(_source, _timeline.KeptRanges, w, h, fps, interBitrate, intermediate, compositor, stage1, ct), ct);

            // Stage 2 — encode the already trimmed/scaled intermediate to the chosen preset (50..100%).
            var interSource = new RecordingSource(intermediate, w, h, fps, TimeSpan.FromMilliseconds(_timeline.KeptDurationMs));
            var cmd = ExportCommand.Build(interSource, new Timeline(interSource).KeptRanges, profile, hardware, outputPath);
            var stage2 = new Progress<double>(v => progress.Report(0.5 + v * 0.5));
            await new VideoExporter(_ffmpegPath).ExportAsync(cmd, _timeline.KeptDurationMs, stage2, ct);
        }
        finally
        {
            try { if (File.Exists(intermediate)) File.Delete(intermediate); } catch { /* best effort */ }
        }
    }

    private void Finish(string outputPath, bool copyToClipboard)
    {
        if (copyToClipboard)
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            ClipboardImage.SetFileDrop(hwnd, outputPath);
            SetRunning(false, "Copied — paste into Slack or Explorer.");
        }
        else if (File.Exists(outputPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{outputPath}\"") { UseShellExecute = true });
        }
        Close();
    }

    private void Fail(Exception ex)
    {
        var msg = ex.Message.Length > 160 ? ex.Message[..160] + "…" : ex.Message;
        SetRunning(false, "Export failed: " + msg);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (_running) _exportCts?.Cancel();
        else Close();
    }

    private void SetRunning(bool running, string status)
    {
        _running = running;
        _progressPanel.IsVisible = true;
        _statusText.Text = status;
        _saveButton.IsEnabled = _copyButton.IsEnabled = _presetBox.IsEnabled = !running;
        _cancelButton.Content = running ? "Stop" : "Close";
    }

    // ---- helpers ----

    private static long? TryFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : null; } catch { return null; }
    }

    private static string HumanBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v < 10 && u > 0 ? $"{v:0.0} {units[u]}" : $"{v:0} {units[u]}";
    }

    private static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }
}
