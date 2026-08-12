using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Shrike.App.Imaging;
using Shrike.App.Native;
using Shrike.Core.Capture;
using Shrike.Core.Imaging;

namespace Shrike.App.Views;

/// <summary>
/// The screenshot editor: shows a capture and lets the user copy it (PNG + DIB) or save it
/// (PNG/JPG/WebP). M1 is view + output only; the annotation toolbox lands in M2. The window is reused
/// across captures so re-opening is instant.
/// </summary>
public partial class EditorWindow : Window
{
    private CapturedImage? _capture;
    private string? _lastSavedPath;

    private Image? _preview;
    private TextBlock? _dimensions;
    private TextBlock? _status;
    private Button? _openFolderButton;
    private Button? _copyPathButton;

    public EditorWindow()
    {
        InitializeComponent();
        _preview = this.FindControl<Image>("Preview");
        _dimensions = this.FindControl<TextBlock>("Dimensions");
        _status = this.FindControl<TextBlock>("Status");
        _openFolderButton = this.FindControl<Button>("OpenFolderButton");
        _copyPathButton = this.FindControl<Button>("CopyPathButton");
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Load a new capture into the editor, resetting per-capture state.</summary>
    public void SetCapture(CapturedImage image)
    {
        _capture = image;
        _lastSavedPath = null;

        if (_preview is not null) _preview.Source = BitmapConverter.ToBitmap(image);
        if (_dimensions is not null) _dimensions.Text = $"{image.Width} × {image.Height}";
        if (_openFolderButton is not null) _openFolderButton.IsEnabled = false;
        if (_copyPathButton is not null) _copyPathButton.IsEnabled = false;
        SetStatus("Annotation tools arrive in M2 — for now: copy or save.");
    }

    private void OnCopy(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_capture is null || !OperatingSystem.IsWindows())
            return;

        var png = ImageCodec.Encode(_capture, ImageFormatKind.Png);
        var dib = ImageCodec.ToDibV5(_capture);
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

        SetStatus(ClipboardImage.Set(hwnd, png, dib)
            ? "Copied to clipboard — PNG + bitmap."
            : "Couldn't reach the clipboard — try again.");
    }

    private async void OnSave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_capture is null)
            return;

        var suggested = CaptureNaming.Expand(CaptureNaming.DefaultTemplate, _capture.CapturedAt);
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
            var bytes = ImageCodec.Encode(_capture, format);
            await File.WriteAllBytesAsync(path, bytes);

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

    private void OnOpenFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_lastSavedPath is null || !OperatingSystem.IsWindows())
            return;

        // Open Explorer with the saved file selected.
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastSavedPath}\"")
        {
            UseShellExecute = true,
        });
    }

    private void OnCopyPath(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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
