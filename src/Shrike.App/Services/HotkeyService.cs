using System.Runtime.Versioning;
using Shrike.App.Native;
using Shrike.Core.Hotkeys;
using Shrike.Core.Ipc;

namespace Shrike.App.Services;

/// <summary>
/// Owns the global hotkey registrations for the resident app. Must be constructed and started on the
/// Avalonia UI thread (see <see cref="MessageWindow"/>). M1 registers the four capture modes; M6
/// makes the whole set rebindable from settings.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HotkeyService : IDisposable
{
    private MessageWindow? _window;
    private readonly Dictionary<int, CaptureAction> _actionsById = [];

    /// <summary>Raised on the UI thread when a registered hotkey fires.</summary>
    public event Action<CaptureAction>? Triggered;

    public void Start()
    {
        _window = new MessageWindow("ShrikeHotkeyWindow");
        _window.HotkeyPressed += OnHotkeyPressed;

        Register(1, Hotkey.DefaultRegion, CaptureAction.CaptureRegion);
        Register(2, Hotkey.DefaultWindow, CaptureAction.CaptureWindow);
        Register(3, Hotkey.DefaultMonitor, CaptureAction.CaptureMonitor);
        Register(4, Hotkey.DefaultFullScreen, CaptureAction.CaptureFullScreen);
    }

    private void Register(int id, Hotkey hotkey, CaptureAction action)
    {
        // A false result (e.g. another app already owns the combo) is non-fatal — that mode just won't
        // have a live global key this session; the tray menu still triggers it.
        if (_window!.RegisterHotkey(id, hotkey.ToWin32Modifiers(), hotkey.ToVirtualKey()))
            _actionsById[id] = action;
    }

    private void OnHotkeyPressed(int id)
    {
        if (_actionsById.TryGetValue(id, out var action))
            Triggered?.Invoke(action);
    }

    public void Dispose()
    {
        _window?.Dispose();
        _window = null;
    }
}
