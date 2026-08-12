using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Shrike.App.Controls;
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

    private AnnotationSurface? _surface;
    private TextBlock? _dimensions;
    private TextBlock? _status;
    private Button? _openFolderButton;
    private Button? _copyPathButton;

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
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Load a new capture into the editor, resetting annotations + per-capture state.</summary>
    public void SetCapture(CapturedImage image)
    {
        _capture = image;
        _lastSavedPath = null;
        _document = new AnnotationDocument();

        _surface?.SetContent(image, _document);
        if (_surface is not null) _surface.Tool = AnnotationTool.None;

        if (_dimensions is not null) _dimensions.Text = $"{image.Width} × {image.Height}";
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
            SetStatus(tool == AnnotationTool.None ? "Select" : $"Tool: {tool}");
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

    private void OnUndo(object? sender, RoutedEventArgs e) => _document.Undo();
    private void OnRedo(object? sender, RoutedEventArgs e) => _document.Redo();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _document.Redo();
            else if (e.Key == Key.Z) _document.Undo();
            else if (e.Key == Key.Y) _document.Redo();
        }
    }

    // ---- export ----

    /// <summary>Flatten the capture + annotations, then apply the destructive redaction scrub.</summary>
    private CapturedImage? BuildExport()
    {
        var flattened = _surface?.RenderFlattened() ?? _capture;
        if (flattened is null) return null;
        return Redaction.Apply(flattened, _document.RedactionRects());
    }

    private void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows() || BuildExport() is not { } image)
            return;

        var png = ImageCodec.Encode(image, ImageFormatKind.Png);
        var dib = ImageCodec.ToDibV5(image);
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

        SetStatus(ClipboardImage.Set(hwnd, png, dib)
            ? "Copied to clipboard — PNG + bitmap."
            : "Couldn't reach the clipboard — try again.");
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (BuildExport() is not { } image)
            return;

        var suggested = CaptureNaming.Expand(CaptureNaming.DefaultTemplate, image.CapturedAt);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save capture",
            SuggestedFileName = suggested,
            DefaultExtension = "png",
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
