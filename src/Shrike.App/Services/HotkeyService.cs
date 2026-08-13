using System.Runtime.Versioning;
using Shrike.App.Native;
using Shrike.Core.Hotkeys;

namespace Shrike.App.Services;

/// <summary>
/// Owns the app's global hotkeys. Two rebindable shortcuts: one opens the capture chooser (default
/// <c>Alt+Shift+Q</c>), one jumps straight to record-region (default <c>Alt+Shift+R</c>); either can be
/// unbound. <see cref="Apply"/> re-registers from the current settings, so a rebind in the settings window
/// takes effect immediately. Must be constructed and driven on the Avalonia UI thread (see
/// <see cref="MessageWindow"/>).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HotkeyService : IDisposable
{
    private const int CaptureHotkeyId = 1;
    private const int RecordHotkeyId = 2;

    private MessageWindow? _window;

    /// <summary>Raised on the UI thread when the capture-chooser hotkey fires.</summary>
    public event Action? CaptureRequested;

    /// <summary>Raised on the UI thread when the record-region hotkey fires.</summary>
    public event Action? RecordRequested;

    public void Start()
    {
        _window = new MessageWindow("ShrikeHotkeyWindow");
        _window.HotkeyPressed += OnHotkeyPressed;
    }

    /// <summary>Register (or re-register) the two hotkeys from their settings strings. Bad/empty strings
    /// are simply left unbound — never fatal.</summary>
    public void Apply(string? captureHotkey, string? recordHotkey)
    {
        if (_window is null) return;

        _window.UnregisterHotkey(CaptureHotkeyId);
        _window.UnregisterHotkey(RecordHotkeyId);

        Register(CaptureHotkeyId, captureHotkey);
        Register(RecordHotkeyId, recordHotkey);
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
            case RecordHotkeyId: RecordRequested?.Invoke(); break;
        }
    }

    public void Dispose()
    {
        _window?.Dispose();
        _window = null;
    }
}
