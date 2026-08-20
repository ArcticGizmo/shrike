using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Shrike.App.Services;
using Shrike.App.Updates;
using Shrike.Core.Changelog;
using Shrike.Core.Hotkeys;
using Shrike.Core.Imaging;
using Shrike.Core.Recording;
using Shrike.Core.Settings;

namespace Shrike.App.Views;

/// <summary>
/// The settings window. Loads the current <see cref="AppSettings"/> into the controls, validates the
/// hotkey text on save (an empty hotkey means "unbound"), and hands the new values to
/// <see cref="SettingsService.Update"/> — which persists them, applies autostart, and re-registers the
/// hotkeys live. Ring size takes effect on the next launch (the ring is memory-only), which the UI notes.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;

    private TextBox _captureBox = null!, _saveDirBox = null!;
    private ComboBox _desktopBox = null!, _formatBox = null!;
    private NumericUpDown _ringSizeBox = null!;
    private CheckBox _autostartBox = null!, _changelogBox = null!;
    private TextBlock _errorText = null!;

    // About + updates (merged in from the old About window).
    private TextBlock _versionText = null!, _updateStatus = null!;
    private Button _checkButton = null!, _applyButton = null!;
    private UpdateCheckResult? _pendingUpdate;

    // Parameterless ctor for the XAML designer only.
    public SettingsWindow() : this(null!) { }

    internal SettingsWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();

        _captureBox = this.FindControl<TextBox>("CaptureHotkeyBox")!;
        _saveDirBox = this.FindControl<TextBox>("SaveDirBox")!;
        _desktopBox = this.FindControl<ComboBox>("DesktopBox")!;
        _formatBox = this.FindControl<ComboBox>("FormatBox")!;
        _ringSizeBox = this.FindControl<NumericUpDown>("RingSizeBox")!;
        _autostartBox = this.FindControl<CheckBox>("AutostartBox")!;
        _changelogBox = this.FindControl<CheckBox>("ChangelogBox")!;
        _errorText = this.FindControl<TextBlock>("ErrorText")!;

        _versionText = this.FindControl<TextBlock>("VersionText")!;
        _updateStatus = this.FindControl<TextBlock>("UpdateStatus")!;
        _checkButton = this.FindControl<Button>("CheckButton")!;
        _applyButton = this.FindControl<Button>("ApplyButton")!;

        _versionText.Text = "v" + AppVersion.Current;

        _desktopBox.ItemsSource = new[] { "Bring the window to me", "Open a new window here" };
        _formatBox.ItemsSource = new[] { "PNG", "JPEG", "WebP" };

        if (settings is not null) LoadFrom(settings.Current);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void LoadFrom(AppSettings s)
    {
        _captureBox.Text = s.CaptureHotkey;
        _desktopBox.SelectedIndex = s.DesktopBehaviour == DesktopBehaviour.NewWindowHere ? 1 : 0;
        _ringSizeBox.Value = s.RingSize;
        _formatBox.SelectedIndex = (int)s.DefaultImageFormat;   // Png=0, Jpeg=1, WebP=2
        _saveDirBox.Text = s.DefaultSaveDirectory ?? "";
        _autostartBox.IsChecked = s.Autostart;
        _changelogBox.IsChecked = s.ShowChangelogOnUpdate;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Default save folder",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            _saveDirBox.Text = path;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        // The hotkey must parse (empty = unbound). Show the problem rather than silently dropping it.
        if (!ValidateHotkey(_captureBox.Text, "Capture"))
            return;

        var dir = string.IsNullOrWhiteSpace(_saveDirBox.Text) ? null : _saveDirBox.Text!.Trim();

        var updated = _settings.Current with
        {
            CaptureHotkey = (_captureBox.Text ?? "").Trim(),
            DesktopBehaviour = _desktopBox.SelectedIndex == 1 ? DesktopBehaviour.NewWindowHere : DesktopBehaviour.FollowMe,
            RingSize = (int)(_ringSizeBox.Value ?? _settings.Current.RingSize),
            DefaultImageFormat = (ImageFormatKind)Math.Max(0, _formatBox.SelectedIndex),
            DefaultSaveDirectory = dir,
            Autostart = _autostartBox.IsChecked == true,
            ShowChangelogOnUpdate = _changelogBox.IsChecked == true,
        };

        _settings.Update(updated);
        Close();
    }

    private bool ValidateHotkey(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;   // unbound is fine
        try { Hotkey.Parse(text); return true; }
        catch
        {
            _errorText.Text = $"{label} hotkey isn't valid — use modifiers + a letter/number/Fn key, e.g. Alt+Shift+Q.";
            _errorText.IsVisible = true;
            return false;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    // ---- About + updates (merged in from the old About window) ----

    /// <summary>Trigger an update check from outside (the tray's "Check for updates…").</summary>
    public void CheckForUpdates() => _ = RunUpdateCheck();

    private void OnCheckUpdates(object? sender, RoutedEventArgs e) => _ = RunUpdateCheck();

    private async Task RunUpdateCheck()
    {
        _checkButton.IsEnabled = false;
        _updateStatus.Text = "Checking…";
        _applyButton.IsVisible = false;
        _pendingUpdate = null;

        var result = await UpdateChecker.CheckDetailedAsync();

        _updateStatus.Text = result.Availability switch
        {
            UpdateAvailability.Available => $"v{result.AvailableVersion} is available (you have v{result.CurrentVersion}).",
            UpdateAvailability.UpToDate => "You're on the latest version.",
            UpdateAvailability.NotApplicable => "Updates apply to installed builds only.",
            _ => "Couldn't check right now.",
        };

        if (result.Availability == UpdateAvailability.Available)
        {
            _pendingUpdate = result;
            _applyButton.IsVisible = true;
        }

        _checkButton.IsEnabled = true;
    }

    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;
        _applyButton.IsEnabled = false;
        _updateStatus.Text = "Downloading update…";
        await UpdateChecker.ApplyAsync(_pendingUpdate);   // restarts the app on success
    }

    /// <summary>Open the same "what's new" card the app pops after an update — here on demand, showing the
    /// full changelog. "Don't show changelogs again" flips the same setting the post-update popup does.</summary>
    private async void OnViewChangelog(object? sender, RoutedEventArgs e)
    {
        var markdown = ChangelogView.LoadEmbedded();
        var sections = markdown is null
            ? Array.Empty<ChangelogSection>()
            : ChangelogParser.Parse(markdown);

        var win = new ChangelogWindow("What's new in Shrike", $"Shrike v{AppVersion.Current}", sections,
            onSuppress: () =>
            {
                if (_settings is { } s) s.Update(s.Current with { ShowChangelogOnUpdate = false });
            });
        await win.ShowDialog(this);
    }

    // Opens the transcription-model manager. The chosen default is persisted immediately (independent of this
    // dialog's Save/Cancel) since model management is its own action.
    private async void OnManageModels(object? sender, RoutedEventArgs e)
    {
        var store = new WhisperModelStore();
        var dlg = new WhisperModelWindow(store, _settings.Current.CaptionModelId,
            id => _settings.Update(_settings.Current with { CaptionModelId = id }));
        await dlg.ShowDialog(this);
    }
}
