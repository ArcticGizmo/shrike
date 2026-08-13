using System.IO;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Shrike.App.Updates;

namespace Shrike.App.Views;

/// <summary>
/// About + changelog + updates. Shows the running version and the embedded <c>CHANGELOG.md</c>, and lets
/// the user check for a newer release (via <see cref="UpdateChecker"/>) and install it. On a dev build
/// (not installed via Velopack) the check simply reports that updates don't apply.
/// </summary>
public partial class AboutWindow : Window
{
    private UpdateCheckResult? _pending;

    private TextBlock _version = null!, _changelog = null!, _status = null!;
    private Button _checkButton = null!, _applyButton = null!;

    public AboutWindow()
    {
        InitializeComponent();

        _version = this.FindControl<TextBlock>("VersionText")!;
        _changelog = this.FindControl<TextBlock>("ChangelogText")!;
        _status = this.FindControl<TextBlock>("UpdateStatus")!;
        _checkButton = this.FindControl<Button>("CheckButton")!;
        _applyButton = this.FindControl<Button>("ApplyButton")!;

        _version.Text = "v" + CurrentVersion();
        _changelog.Text = LoadChangelog();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private static string CurrentVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // Informational version can carry a +build suffix; trim it for display.
        if (!string.IsNullOrEmpty(info))
            return info.Split('+')[0];
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static string LoadChangelog()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
            if (stream is null) return "Changelog unavailable.";
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return "Changelog unavailable.";
        }
    }

    private async void OnCheckUpdates(object? sender, RoutedEventArgs e)
    {
        _checkButton.IsEnabled = false;
        _status.Text = "Checking…";
        _applyButton.IsVisible = false;
        _pending = null;

        var result = await UpdateChecker.CheckDetailedAsync();

        _status.Text = result.Availability switch
        {
            UpdateAvailability.Available => $"v{result.AvailableVersion} is available (you have v{result.CurrentVersion}).",
            UpdateAvailability.UpToDate => "You're on the latest version.",
            UpdateAvailability.NotApplicable => "Updates apply to installed builds only.",
            _ => "Couldn't check right now.",
        };

        if (result.Availability == UpdateAvailability.Available)
        {
            _pending = result;
            _applyButton.IsVisible = true;
        }

        _checkButton.IsEnabled = true;
    }

    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_pending is null) return;
        _applyButton.IsEnabled = false;
        _status.Text = "Downloading update…";
        await UpdateChecker.ApplyAsync(_pending);   // restarts the app on success
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
