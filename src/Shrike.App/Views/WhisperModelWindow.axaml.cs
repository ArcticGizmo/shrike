using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Shrike.Core.Recording;

namespace Shrike.App.Views;

/// <summary>
/// The transcription-model manager — the opt-in, in-app download surface for whisper models (they are never
/// shipped in the installer). Lists the catalog with each model's size and installed state, downloads one
/// with a progress bar (cancellable), lets the user pick which installed model captions use, and deletes one
/// to reclaim disk. The chosen default is persisted through an injected callback so this window stays
/// decoupled from the settings service.
/// </summary>
public partial class WhisperModelWindow : Window
{
    private readonly WhisperModelStore _store;
    private readonly WhisperEngineInstaller _engine;
    private readonly Action<string?> _onDefaultChanged;
    private string? _defaultId;

    private ComboBox _modelBox = null!;
    private ProgressBar _downloadBar = null!;
    private TextBlock _statusText = null!;
    private Button _primaryButton = null!, _deleteButton = null!, _closeButton = null!;
    private Border _engineRow = null!;
    private Button _engineButton = null!;

    private CancellationTokenSource? _cts;
    private bool _busy;

    // Parameterless ctor for the XAML designer only.
    public WhisperModelWindow() : this(new WhisperModelStore(), null, _ => { }) { }

    internal WhisperModelWindow(WhisperModelStore store, string? defaultId, Action<string?> onDefaultChanged,
        WhisperEngineInstaller? engine = null)
    {
        _store = store;
        _engine = engine ?? new WhisperEngineInstaller();
        _defaultId = defaultId;
        _onDefaultChanged = onDefaultChanged;
        InitializeComponent();

        _modelBox = this.FindControl<ComboBox>("ModelBox")!;
        _downloadBar = this.FindControl<ProgressBar>("DownloadBar")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _primaryButton = this.FindControl<Button>("PrimaryButton")!;
        _deleteButton = this.FindControl<Button>("DeleteButton")!;
        _closeButton = this.FindControl<Button>("CloseButton")!;
        _engineRow = this.FindControl<Border>("EngineRow")!;
        _engineButton = this.FindControl<Button>("EngineButton")!;

        // Seed the picker on the remembered default (or the suggested one), then paint state.
        var startId = _defaultId ?? WhisperModelCatalog.DefaultId;
        Refresh(WhisperModelCatalog.Models.ToList().FindIndex(m => m.Id == startId));
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private WhisperModel? Selected =>
        _modelBox.SelectedIndex >= 0 && _modelBox.SelectedIndex < WhisperModelCatalog.Models.Count
            ? WhisperModelCatalog.Models[_modelBox.SelectedIndex]
            : null;

    private void Refresh(int selectIndex = -1)
    {
        var keep = selectIndex >= 0 ? selectIndex : Math.Max(0, _modelBox.SelectedIndex);
        _modelBox.ItemsSource = WhisperModelCatalog.Models.Select(m =>
        {
            var marks = _store.IsInstalled(m) ? "installed" : m.ApproxSize;
            var isDefault = m.Id == _defaultId && _store.IsInstalled(m);
            return $"{m.DisplayName} — {marks}{(isDefault ? "  ✓ in use" : "")}";
        }).ToList();
        _modelBox.SelectedIndex = Math.Clamp(keep, 0, WhisperModelCatalog.Models.Count - 1);
        UpdateState();
    }

    private void UpdateState()
    {
        var model = Selected;
        var installed = model is not null && _store.IsInstalled(model);

        // The engine-install prompt shows whenever the whisper binary isn't resolvable yet.
        _engineRow.IsVisible = !Whisper.IsAvailable;

        if (_busy)
        {
            _primaryButton.Content = "Cancel";
            _primaryButton.IsEnabled = true;
            _deleteButton.IsEnabled = false;
            _closeButton.IsEnabled = false;
            _engineButton.IsEnabled = false;
            return;
        }

        _downloadBar.IsVisible = false;
        _closeButton.IsEnabled = true;
        _engineButton.IsEnabled = true;

        if (model is null) { _primaryButton.IsEnabled = false; _deleteButton.IsEnabled = false; return; }

        if (installed)
        {
            var isDefault = model.Id == _defaultId;
            _primaryButton.Content = isDefault ? "In use" : "Use for captions";
            _primaryButton.IsEnabled = !isDefault;
            _deleteButton.IsEnabled = true;
        }
        else
        {
            _primaryButton.Content = $"Download ({model.ApproxSize})";
            _primaryButton.IsEnabled = true;
            _deleteButton.IsEnabled = false;
        }

        var currentName = WhisperModelCatalog.Find(_defaultId) is { } d && _store.InstalledPath(_defaultId) is not null
            ? d.DisplayName
            : null;
        _statusText.Text = currentName is not null
            ? $"In use for captions: {currentName}."
            : "No caption model chosen yet.";
    }

    private async void OnInstallEngine(object? sender, RoutedEventArgs e)
    {
        if (_busy) { _cts?.Cancel(); return; }
        _busy = true;
        _cts = new CancellationTokenSource();
        _downloadBar.IsVisible = true;
        _downloadBar.Value = 0;
        _statusText.Text = $"Downloading transcription engine ({WhisperEngine.ApproxSize})…";
        _engineButton.Content = "Cancel";
        UpdateState();

        var progress = new Progress<double>(v => _downloadBar.Value = v);
        try
        {
            await _engine.DownloadAsync(progress, _cts.Token);
            _statusText.Text = "Transcription engine installed. Now download a model below.";
        }
        catch (OperationCanceledException)
        {
            _statusText.Text = "Engine download cancelled.";
        }
        catch (Exception ex)
        {
            _statusText.Text = "Couldn't install the engine: " + ex.Message;
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            _engineButton.Content = "Install engine";
            Refresh();
        }
    }

    private void OnModelChanged(object? sender, SelectionChangedEventArgs e) => UpdateState();

    private async void OnPrimary(object? sender, RoutedEventArgs e)
    {
        if (_busy) { _cts?.Cancel(); return; }
        if (Selected is not { } model) return;

        if (_store.IsInstalled(model))
        {
            // Already downloaded → just make it the caption model.
            SetDefault(model.Id);
            Refresh();
            return;
        }

        await DownloadAsync(model);
    }

    private async Task DownloadAsync(WhisperModel model)
    {
        _busy = true;
        _cts = new CancellationTokenSource();
        _downloadBar.IsVisible = true;
        _downloadBar.Value = 0;
        _statusText.Text = $"Downloading {model.DisplayName} ({model.ApproxSize})…";
        UpdateState();

        var progress = new Progress<double>(v => _downloadBar.Value = v);
        try
        {
            await _store.DownloadAsync(model, progress, _cts.Token);
            SetDefault(model.Id); // a freshly downloaded model becomes the one captions use
            _statusText.Text = $"Downloaded {model.DisplayName}. It's now your caption model.";
        }
        catch (OperationCanceledException)
        {
            _statusText.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            _statusText.Text = "Couldn't download that model: " + ex.Message;
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            Refresh();
        }
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not { } model || !_store.IsInstalled(model)) return;
        _store.Delete(model);
        if (_defaultId == model.Id) SetDefault(null); // it was the caption model — clear it
        _statusText.Text = $"Deleted {model.DisplayName}.";
        Refresh();
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (_busy) { _cts?.Cancel(); return; }
        Close();
    }

    private void SetDefault(string? id)
    {
        _defaultId = id;
        _onDefaultChanged(id);
    }

    /// <summary>
    /// Ensure transcription is ready — both the engine binary and a model — returning the path of the model
    /// captions should use, or null if the user closes the setup without completing it. Returns immediately
    /// (no UI) only when the engine is present <b>and</b> a model is installed; otherwise opens this window so
    /// the user can install whatever is missing (engine and/or model) from within the app.
    /// </summary>
    public static async Task<string?> EnsureModelAsync(
        Window owner, WhisperModelStore store, string? defaultId, Action<string?> onDefaultChanged)
    {
        string? ModelPath(string? id) => store.InstalledPath(id)
            ?? (store.Installed().FirstOrDefault() is { } m ? store.PathFor(m) : null);

        if (Whisper.IsAvailable && ModelPath(defaultId) is { } ready) return ready;

        var dlg = new WhisperModelWindow(store, defaultId, onDefaultChanged);
        await dlg.ShowDialog(owner);
        Whisper.ResetCache(); // the engine may have just been installed in the dialog
        return Whisper.IsAvailable ? ModelPath(dlg._defaultId) : null;
    }
}
