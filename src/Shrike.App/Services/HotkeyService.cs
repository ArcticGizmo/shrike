using System.Runtime.Versioning;
using Shrike.App.Native;
using Shrike.Core.Hotkeys;

namespace Shrike.App.Services;

/// <summary>
/// Owns the app's global hotkey: a single rebindable shortcut that opens the capture chooser (default
/// <c>Alt+Shift+Q</c>), which can be unbound. <see cref="Apply"/> re-registers from the current settings,
/// so a rebind in the settings window takes effect immediately. Must be constructed and driven on the
/// Avalonia UI thread (see <see cref="MessageWindow"/>).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HotkeyService : IDisposable
{
    private const int CaptureHotkeyId = 1;

    private MessageWindow? _window;

    /// <summary>Raised on the UI thread when the capture-chooser hotkey fires.</summary>
    public event Action? CaptureRequested;

    public void Start()
    {
        _window = new MessageWindow("ShrikeHotkeyWindow");
        _window.HotkeyPressed += OnHotkeyPressed;
    }

    /// <summary>Register (or re-register) the capture hotkey from its settings string. A bad/empty string
    /// is simply left unbound — never fatal.</summary>
    public void Apply(string? captureHotkey)
    {
        if (_window is null) return;

        _window.UnregisterHotkey(CaptureHotkeyId);
        Register(CaptureHotkeyId, captureHotkey);
    }

    private void Register(int id, string? text)
    {
        if (TryParse(text) is not { } hk) return;
        // A false result (another app owns the combo) is non-fatal — the tray still works.
        _window!.RegisterHotkey(id, hk.ToWin32Modifiers(), hk.ToVirtualKey());
    }

    private static Hotkey? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return Hotkey.Parse(text); } catch { return null; }
    }

    private void OnHotkeyPressed(int id)
    {
        switch (id)
        {
            case CaptureHotkeyId: CaptureRequested?.Invoke(); break;
        }
    }

    public void Dispose()
    {
        _window?.Dispose();
        _window = null;
    }
}
