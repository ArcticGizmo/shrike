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

    private async Task Encode(ExportProfile profile, HwEncoder? hardware, string outputPath,
        IProgress<double> progress, CancellationToken ct)
    {
        var cmd = ExportCommand.Build(_source, _timeline.KeptRanges, profile, hardware, outputPath);
        await new VideoExporter(_ffmpegPath).ExportAsync(cmd, _timeline.KeptDurationMs, progress, ct);
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
