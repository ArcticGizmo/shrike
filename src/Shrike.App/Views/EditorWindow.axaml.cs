using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Shrike.App.Controls;
using Shrike.App.Imaging;
using Shrike.App.Native;
using Shrike.Core.Annotations;
using Shrike.Core.Capture;
using Shrike.Core.Imaging;

namespace Shrike.App.Views;

/// <summary>
/// The screenshot editor: shows a capture, lets the user annotate it (M2), and copy (PNG + DIB) or
/// save it (PNG/JPG/WebP). Export flattens the capture + annotations, then applies the destructive
/// redaction scrub. The window is reused across captures so re-opening is instant.
/// </summary>
public partial class EditorWindow : Window
{
    private CapturedImage? _capture;
    private AnnotationDocument _document = new();
    private string? _lastSavedPath;

    private RecentRing? _ring;
    private Action<CapturedImage>? _openInEditor;

    private AnnotationSurface? _surface;
    private TextBlock? _dimensions;
    private TextBlock? _status;
    private Button? _openFolderButton;
    private Button? _copyPathButton;
    private Button? _zoomLabel;
    private Border? _recentStrip;
    private StackPanel? _recentItems;

    private readonly List<Button> _toolButtons = [];
    private readonly List<Button> _strokeButtons = [];
    private readonly List<Button> _swatchButtons = [];
    private bool _buttonsCollected;

    public EditorWindow()
    {
        InitializeComponent();
        _surface = this.FindControl<AnnotationSurface>("Surface");
        _dimensions = this.FindControl<TextBlock>("Dimensions");
        _status = this.FindControl<TextBlock>("Status");
        _openFolderButton = this.FindControl<Button>("OpenFolderButton");
        _copyPathButton = this.FindControl<Button>("CopyPathButton");
        _zoomLabel = this.FindControl<Button>("ZoomLabel");
        _recentStrip = this.FindControl<Border>("RecentStrip");
        _recentItems = this.FindControl<StackPanel>("RecentItems");

        if (_surface is not null)
        {
            _surface.ZoomChanged += RefreshZoomLabel;
            _surface.CropChanged += RefreshDimensions;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Bind the editor's recent filmstrip to the shared ring. Idempotent — the controller calls this on
    /// every capture, but we only subscribe once. <paramref name="openInEditor"/> re-opens a ring item
    /// without pushing a duplicate entry.
    /// </summary>
    public void AttachRecentRing(RecentRing ring, Action<CapturedImage> openInEditor)
    {
        _openInEditor = openInEditor;
        if (ReferenceEquals(_ring, ring))
            return;

        _ring = ring;
        ring.Changed += RebuildRecentStrip;
        RebuildRecentStrip();
    }

    /// <summary>Load a new capture into the editor, resetting annotations + per-capture state.</summary>
    public void SetCapture(CapturedImage image)
    {
        _capture = image;
        _lastSavedPath = null;
        _document = new AnnotationDocument();

        _surface?.SetContent(image, _document);
        if (_surface is not null) _surface.Tool = AnnotationTool.None;

        RefreshDimensions();
        if (_openFolderButton is not null) _openFolderButton.IsEnabled = false;
        if (_copyPathButton is not null) _copyPathButton.IsEnabled = false;
        RefreshActiveStates();
        SetStatus("Pick a tool and drag to annotate · Ctrl+Z to undo");
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RefreshActiveStates(); // reflect the surface's tool/stroke/colour once the tree exists
    }

    // ---- toolbar ----

    private void OnToolClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && _surface is not null
            && Enum.TryParse<AnnotationTool>(tag, out var tool))
        {
            _surface.Tool = tool;
            SetStatus(tool switch
            {
                AnnotationTool.None => "Select",
                AnnotationTool.Crop => "Drag to set the crop · click (tiny drag) to clear it",
                _ => $"Tool: {tool}",
            });
            RefreshActiveStates();
        }
    }

    private void OnColorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex } && _surface is not null)
        {
            _surface.StrokeColorHex = hex;
            SetStatus($"Colour {hex}");
            RefreshActiveStates();
        }
    }

    private void OnStrokeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string width } && _surface is not null
            && double.TryParse(width, out var w))
        {
            _surface.StrokeWidth = w;
            SetStatus($"Stroke {w:0}px");
            RefreshActiveStates();
        }
    }

    /// <summary>Highlight the tool/stroke/colour buttons that match the surface's current settings.</summary>
    private void RefreshActiveStates()
    {
        if (_surface is null) return;
        EnsureButtonsCollected();

        foreach (var b in _toolButtons)
            b.Classes.Set("active", b.Tag is string t && Enum.TryParse<AnnotationTool>(t, out var tool) && tool == _surface.Tool);

        foreach (var b in _strokeButtons)
            b.Classes.Set("active", b.Tag is string t && double.TryParse(t, out var w) && Math.Abs(w - _surface.StrokeWidth) < 0.01);

        foreach (var b in _swatchButtons)
            b.Classes.Set("active", b.Tag is string hex && string.Equals(hex, _surface.StrokeColorHex, StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureButtonsCollected()
    {
        if (_buttonsCollected) return;
        _toolButtons.Clear();
        _strokeButtons.Clear();
        _swatchButtons.Clear();
        Collect(this);
        // Only mark collected once the visual tree actually yielded buttons.
        if (_toolButtons.Count > 0) _buttonsCollected = true;

        void Collect(Visual root)
        {
            foreach (var child in root.GetVisualChildren())
            {
                if (child is Button b)
                {
                    if (b.Classes.Contains("tool")) _toolButtons.Add(b);
                    else if (b.Classes.Contains("stroke")) _strokeButtons.Add(b);
                    else if (b.Classes.Contains("swatch")) _swatchButtons.Add(b);
                }
                Collect(child);
            }
        }
    }

    private void OnUndo(object? sender, RoutedEventArgs e) => Undo();
    private void OnRedo(object? sender, RoutedEventArgs e) => Redo();

    // Undo/redo can invalidate the selection's index, so drop it first.
    private void Undo() { _surface?.ClearSelection(); _document.Undo(); }
    private void Redo() { _surface?.ClearSelection(); _document.Redo(); }

    // ---- zoom ----

    private void OnZoomIn(object? sender, RoutedEventArgs e) => _surface?.ZoomIn();
    private void OnZoomOut(object? sender, RoutedEventArgs e) => _surface?.ZoomOut();
    private void OnZoomFit(object? sender, RoutedEventArgs e) => _surface?.ZoomToFit();
    private void OnZoomActual(object? sender, RoutedEventArgs e) => _surface?.ZoomToActual();

    /// <summary>Reflect the surface's zoom in the toolbar button ("Fit" or a percentage).</summary>
    private void RefreshZoomLabel()
    {
        if (_zoomLabel is null || _surface is null) return;
        _zoomLabel.Content = _surface.IsFit ? "Fit" : $"{_surface.ZoomPercent:0}%";
    }

    /// <summary>Show the export size in the readout, flagging when a crop is active.</summary>
    private void RefreshDimensions()
    {
        if (_dimensions is null || _surface is null) return;
        var (w, h) = _surface.EffectiveSize();
        _dimensions.Text = _surface.IsCropped ? $"{w} × {h} (cropped)" : $"{w} × {h}";
    }

    // ---- recent filmstrip ----

    /// <summary>Rebuild the thumbnail strip from the ring (newest first); hide it when the ring is empty.</summary>
    private void RebuildRecentStrip()
    {
        if (_recentItems is null || _recentStrip is null || _ring is null)
            return;

        _recentItems.Children.Clear();

        if (_ring.Count == 0)
        {
            _recentStrip.IsVisible = false;
            return;
        }

        foreach (var item in _ring.Items)
            _recentItems.Children.Add(BuildThumbButton(item));

        _recentStrip.IsVisible = true;
    }

    private Button BuildThumbButton(RecentCapture item)
    {
        var image = new Image
        {
            Source = BitmapConverter.ToBitmap(item.Thumbnail),
            Height = 46,
            Stretch = Avalonia.Media.Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);

        var button = new Button
        {
            Classes = { "recent" },
            Content = image,
            [ToolTip.TipProperty] = $"{item.CapturedAt.LocalDateTime:HH:mm:ss} · {item.Image.Width}×{item.Image.Height}",
        };
        // Click re-opens; the context menu carries copy / save / delete.
        button.Click += (_, _) => _openInEditor?.Invoke(item.Image);

        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) => CopyImage(item.Image);
        var save = new MenuItem { Header = "Save as…" };
        save.Click += async (_, _) => await SaveImageAsync(item.Image);
        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => _ring?.Remove(item);

        button.ContextMenu = new ContextMenu { ItemsSource = new[] { copy, save, delete } };
        return button;
    }

    private void CopyImage(CapturedImage image)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        SetStatus(CaptureClipboard.Copy(hwnd, image)
            ? "Copied to clipboard — PNG + bitmap."
            : "Couldn't reach the clipboard — try again.");
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_ring is not null)
            _ring.Changed -= RebuildRecentStrip;
        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Let the in-place text editor own the keyboard while a label is being typed.
        if (_surface?.IsEditingText == true) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Redo();
            else if (e.Key == Key.Z) Undo();
            else if (e.Key == Key.Y) Redo();
            // Zoom: Ctrl++ / Ctrl+= (in), Ctrl+- (out), Ctrl+0 (fit), Ctrl+1 (100%).
            else if (e.Key is Key.OemPlus or Key.Add) { _surface?.ZoomIn(); e.Handled = true; }
            else if (e.Key is Key.OemMinus or Key.Subtract) { _surface?.ZoomOut(); e.Handled = true; }
            else if (e.Key is Key.D0 or Key.NumPad0) { _surface?.ZoomToFit(); e.Handled = true; }
            else if (e.Key is Key.D1 or Key.NumPad1) { _surface?.ZoomToActual(); e.Handled = true; }
        }
        else if (e.Key is Key.Delete or Key.Back) { _surface?.DeleteSelected(); e.Handled = true; }
    }

    // ---- export ----

    /// <summary>
    /// Flatten the capture + annotations, apply the destructive redaction scrub (in full-image
    /// coordinates), then crop to the export region last.
    /// </summary>
    private CapturedImage? BuildExport()
    {
        var flattened = _surface?.RenderFlattened() ?? _capture;
        if (flattened is null) return null;
        var redacted = Redaction.Apply(flattened, _document.RedactionRects());
        return _surface?.ApplyExportCrop(redacted) ?? redacted;
    }

    private void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (BuildExport() is { } image)
            CopyImage(image);
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (BuildExport() is { } image)
            await SaveImageAsync(image);
    }

    private async Task SaveImageAsync(CapturedImage image)
    {
        var suggested = CaptureNaming.Expand(CaptureNaming.DefaultTemplate, image.CapturedAt);
        var settings = Shrike.App.Services.SettingsService.Instance?.Current;

        var defaultExt = settings?.DefaultImageFormat switch
        {
            Shrike.Core.Imaging.ImageFormatKind.Jpeg => "jpg",
            Shrike.Core.Imaging.ImageFormatKind.WebP => "webp",
            _ => "png",
        };
        IStorageFolder? start = null;
        if (settings?.DefaultSaveDirectory is { } dir && Directory.Exists(dir))
            start = await StorageProvider.TryGetFolderFromPathAsync(dir);

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save capture",
            SuggestedFileName = suggested,
            DefaultExtension = defaultExt,
            SuggestedStartLocation = start,
            FileTypeChoices =
            [
                new FilePickerFileType("PNG image") { Patterns = ["*.png"], MimeTypes = ["image/png"] },
                new FilePickerFileType("JPEG image") { Patterns = ["*.jpg", "*.jpeg"], MimeTypes = ["image/jpeg"] },
                new FilePickerFileType("WebP image") { Patterns = ["*.webp"], MimeTypes = ["image/webp"] },
            ],
        });

        if (file is null)
            return;

        var path = file.TryGetLocalPath() ?? file.Path.LocalPath;
        var format = FormatFromExtension(Path.GetExtension(path));

        try
        {
            await File.WriteAllBytesAsync(path, ImageCodec.Encode(image, format));

            _lastSavedPath = path;
            if (_openFolderButton is not null) _openFolderButton.IsEnabled = true;
            if (_copyPathButton is not null) _copyPathButton.IsEnabled = true;
            SetStatus($"Saved {format} → {path}");
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}");
        }
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (_lastSavedPath is null || !OperatingSystem.IsWindows())
            return;

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastSavedPath}\"")
        {
            UseShellExecute = true,
        });
    }

    private void OnCopyPath(object? sender, RoutedEventArgs e)
    {
        if (_lastSavedPath is null || !OperatingSystem.IsWindows())
            return;

        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        SetStatus(ClipboardImage.SetText(hwnd, _lastSavedPath)
            ? "Path copied to clipboard."
            : "Couldn't reach the clipboard — try again.");
    }

    private static ImageFormatKind FormatFromExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImageFormatKind.Jpeg,
            ".webp" => ImageFormatKind.WebP,
            _ => ImageFormatKind.Png,
        };

    private void SetStatus(string text)
    {
        if (_status is not null) _status.Text = text;
    }
}
