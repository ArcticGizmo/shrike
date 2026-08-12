using System.Runtime.Versioning;
using Shrike.App.Native;
using Shrike.Core.Hotkeys;
using Shrike.Core.Ipc;

namespace Shrike.App.Services;

/// <summary>
/// Owns the global hotkey registrations for the resident app. Must be constructed and started on the
/// Avalonia UI thread (see <see cref="MessageWindow"/>). In M0 it registers a single region-capture
/// hotkey; M6 makes the whole set rebindable from settings.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HotkeyService : IDisposable
{
    private const int RegionHotkeyId = 1;

    private MessageWindow? _window;

    /// <summary>Raised on the UI thread when a registered hotkey fires.</summary>
    public event Action<CaptureAction>? Triggered;

    /// <summary>True if the region hotkey was actually claimed from the OS.</summary>
    public bool RegionHotkeyRegistered { get; private set; }

    public void Start()
    {
        _window = new MessageWindow("ShrikeHotkeyWindow");
        _window.HotkeyPressed += OnHotkeyPressed;

        var hk = Hotkey.DefaultRegion;
        // A false result (e.g. another instance already owns the combo) is non-fatal — the app still
        // runs from the tray; the global key just won't be live this session.
        RegionHotkeyRegistered = _window.RegisterHotkey(RegionHotkeyId, hk.ToWin32Modifiers(), hk.ToVirtualKey());
    }

    private void OnHotkeyPressed(int id)
    {
        if (id == RegionHotkeyId)
            Triggered?.Invoke(CaptureAction.ShowOverlay);
    }

    public void Dispose()
    {
        _window?.Dispose();
        _window = null;
    }
}
