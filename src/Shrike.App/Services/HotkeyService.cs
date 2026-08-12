using System.Runtime.Versioning;
using Shrike.App.Native;
using Shrike.Core.Hotkeys;

namespace Shrike.App.Services;

/// <summary>
/// Owns the app's single global hotkey. Deliberately one shortcut (default <c>Alt+Shift+Q</c>) that
/// opens the capture chooser — most people only remember one key for an occasional-use tool. Must be
/// constructed and started on the Avalonia UI thread (see <see cref="MessageWindow"/>). M6 makes the
/// key rebindable.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HotkeyService : IDisposable
{
    private const int CaptureHotkeyId = 1;

    private MessageWindow? _window;

    /// <summary>Raised on the UI thread when the capture hotkey fires.</summary>
    public event Action? CaptureRequested;

    public void Start()
    {
        _window = new MessageWindow("ShrikeHotkeyWindow");
        _window.HotkeyPressed += OnHotkeyPressed;

        var hk = Hotkey.DefaultCapture;
        // A false result (e.g. another app owns the combo) is non-fatal — the tray still works.
        _window.RegisterHotkey(CaptureHotkeyId, hk.ToWin32Modifiers(), hk.ToVirtualKey());
    }

    private void OnHotkeyPressed(int id)
    {
        if (id == CaptureHotkeyId)
            CaptureRequested?.Invoke();
    }

    public void Dispose()
    {
        _window?.Dispose();
        _window = null;
    }
}
