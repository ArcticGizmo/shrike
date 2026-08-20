using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Shrike.Audio;
using Shrike.Core.Audio;

namespace Shrike.App.Views;

/// <summary>
/// The "mic check" dialog: pick the input device, watch a live level meter, record a 3-second test and hear
/// it back, and arm the mic and/or system-sound loopback — all before recording starts, so a muted or wrong
/// mic is caught up front (the recurring pain point). Every choice is raised as an event the moment it
/// changes so the caller can persist it; the live meter runs only while this window is open. All device I/O
/// is best-effort — a machine with no mic still opens the dialog, just with a flat meter.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class MicCheckWindow : Window
{
    private readonly IBrush _meterNormal = new SolidColorBrush(Color.Parse("#F5A524"));
    private readonly IBrush _meterHot = new SolidColorBrush(Color.Parse("#EF4444"));

    private readonly DispatcherTimer _meterTimer;

    private CheckBox? _micEnabledBox;
    private ComboBox? _micDeviceBox;
    private ProgressBar? _micMeter;
    private Button? _testButton;
    private TextBlock? _testStatus;
    private CheckBox? _systemSoundBox;

    private IReadOnlyList<string?> _deviceIds = [null]; // parallel to the combo; index 0 = system default
    private AudioLevelMonitor? _monitor;
    private NAudioPlayer? _testPlayer;
    private bool _initializing;
    private bool _testRunning;

    /// <summary>Raised when the "record my mic" checkbox flips.</summary>
    public event Action<bool>? MicEnabledChanged;

    /// <summary>Raised when the selected input device changes (null = system default).</summary>
    public event Action<string?>? DeviceChanged;

    /// <summary>Raised when the system-sound checkbox flips.</summary>
    public event Action<bool>? SystemSoundChanged;

    // Parameterless ctor for the XAML designer only.
    public MicCheckWindow() : this(default) { }

    internal MicCheckWindow(MicSetup setup)
    {
        InitializeComponent();

        _micEnabledBox = this.FindControl<CheckBox>("MicEnabledBox");
        _micDeviceBox = this.FindControl<ComboBox>("MicDeviceBox");
        _micMeter = this.FindControl<ProgressBar>("MicMeter");
        _testButton = this.FindControl<Button>("TestButton");
        _testStatus = this.FindControl<TextBlock>("TestStatus");
        _systemSoundBox = this.FindControl<CheckBox>("SystemSoundBox");

        _initializing = true;
        if (_micEnabledBox is not null) _micEnabledBox.IsChecked = setup.MicEnabled;
        if (_systemSoundBox is not null) _systemSoundBox.IsChecked = setup.SystemSound;
        PopulateDevices(setup.MicDeviceId);
        _initializing = false;

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _meterTimer.Tick += (_, _) => UpdateMeter();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        StartMonitoring();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopMonitoring();
        _testPlayer?.Dispose();
        _testPlayer = null;
        base.OnClosed(e);
    }

    // ---- device list ----

    private void PopulateDevices(string? selectedId)
    {
        if (_micDeviceBox is null) return;

        var names = new List<string> { "System default" };
        var ids = new List<string?> { null };
        try
        {
            foreach (var device in new NAudioDeviceCatalog().InputDevices())
            {
                names.Add(device.IsDefault ? $"{device.Name} (default)" : device.Name);
                ids.Add(device.Id);
            }
        }
        catch { /* enumeration failed — just the system-default entry */ }

        _deviceIds = ids;
        _micDeviceBox.ItemsSource = names;
        var index = selectedId is null ? 0 : ids.IndexOf(selectedId);
        _micDeviceBox.SelectedIndex = index < 0 ? 0 : index;
    }

    private string? SelectedDeviceId()
    {
        var i = _micDeviceBox?.SelectedIndex ?? 0;
        return i >= 0 && i < _deviceIds.Count ? _deviceIds[i] : null;
    }

    // ---- events out ----

    private void OnMicEnabledChanged(object? sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        MicEnabledChanged?.Invoke(_micEnabledBox?.IsChecked ?? false);
    }

    private void OnDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        DeviceChanged?.Invoke(SelectedDeviceId());
        if (_monitor is not null) StartMonitoring(); // re-open the meter on the new device
    }

    private void OnSystemSoundChanged(object? sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SystemSoundChanged?.Invoke(_systemSoundBox?.IsChecked ?? false);
    }

    private void OnDone(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Mirror the mic arm state set elsewhere (the HUD toggle) into this dialog's checkbox, without
    /// re-raising the change event. Setting <c>IsChecked</c> doesn't fire <c>Click</c>, and the guard covers it.</summary>
    internal void ReflectMicEnabled(bool on)
    {
        if (_micEnabledBox is null || _micEnabledBox.IsChecked == on) return;
        _initializing = true;
        _micEnabledBox.IsChecked = on;
        _initializing = false;
    }

    /// <summary>Mirror the system-sound arm state set elsewhere (the HUD toggle) into this dialog's checkbox.</summary>
    internal void ReflectSystemSound(bool on)
    {
        if (_systemSoundBox is null || _systemSoundBox.IsChecked == on) return;
        _initializing = true;
        _systemSoundBox.IsChecked = on;
        _initializing = false;
    }

    // ---- live meter ----

    private void StartMonitoring()
    {
        StopMonitoring();
        if (_testRunning) return;
        try
        {
            var source = WasapiAudioSource.Microphone(SelectedDeviceId());
            _monitor = new AudioLevelMonitor(source);
            _monitor.Start();
            _meterTimer.Start();
        }
        catch
        {
            _monitor = null;
            SetStatus("Mic unavailable");
        }
    }

    private void StopMonitoring()
    {
        _meterTimer.Stop();
        _monitor?.Dispose();
        _monitor = null;
        if (_micMeter is not null) _micMeter.Value = 0;
    }

    private void UpdateMeter()
    {
        if (_monitor is null || _micMeter is null) return;
        var level = _monitor.ReadAndDecay();
        _micMeter.Value = level.Peak;
        _micMeter.Foreground = level.Peak >= 0.98 ? _meterHot : _meterNormal;
    }

    // ---- test & play back ----

    private async void OnTest(object? sender, RoutedEventArgs e)
    {
        if (_testRunning) return;
        _testRunning = true;
        StopMonitoring();
        if (_testButton is not null) _testButton.IsEnabled = false;
        SetStatus("Recording…");

        var tmp = Path.Combine(Path.GetTempPath(), $"shrike-mictest-{Guid.NewGuid():N}.wav");
        var deviceId = SelectedDeviceId();
        try
        {
            AudioCaptureRecorder? recorder;
            try
            {
                var source = WasapiAudioSource.Microphone(deviceId);
                recorder = new AudioCaptureRecorder(source, new WavWriter(tmp, source.Format), () => 0L);
                recorder.Start();
            }
            catch { SetStatus("Mic unavailable"); return; }

            await Task.Delay(3000);
            await Task.Run(recorder.Dispose); // finalise the WAV off the UI thread

            SetStatus("Playing…");
            _testPlayer?.Dispose();
            _testPlayer = new NAudioPlayer();
            _testPlayer.Load(tmp);
            _testPlayer.Play();
            await Task.Delay(_testPlayer.Duration + TimeSpan.FromMilliseconds(250));
            _testPlayer.Stop();
            SetStatus("");
        }
        catch { SetStatus("Test failed"); }
        finally
        {
            _testPlayer?.Dispose();
            _testPlayer = null;
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* temp cleanup is best effort */ }
            if (_testButton is not null) _testButton.IsEnabled = true;
            _testRunning = false;
            if (IsVisible) StartMonitoring(); // resume the live meter
        }
    }

    private void SetStatus(string text)
    {
        if (_testStatus is not null) _testStatus.Text = text;
    }
}
