namespace Shrike.Core.Hotkeys;

/// <summary>
/// Modifier flags for a global hotkey. Values are chosen to match the Win32 <c>MOD_*</c> constants
/// so <see cref="Hotkey.ToWin32Modifiers"/> is a straight cast.
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x0001,     // MOD_ALT
    Control = 0x0002, // MOD_CONTROL
    Shift = 0x0004,   // MOD_SHIFT
    Win = 0x0008,     // MOD_WIN
}

/// <summary>
/// A parsed, rebindable global hotkey — modifiers plus a single main key (a letter, digit or Fn key).
/// Round-trips through <see cref="Parse"/> / <see cref="ToString"/> so it can live in settings as text,
/// and exposes the Win32 modifier mask + virtual-key code needed by <c>RegisterHotKey</c>.
/// </summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, string Key)
{
    private const uint MOD_NOREPEAT = 0x4000;

    // The single default capture hotkey (review decision: rebindable Alt+Shift+… to avoid the OS
    // Win+Shift+S). It opens the capture chooser rather than binding one mode.
    private const HotkeyModifiers AltShift = HotkeyModifiers.Alt | HotkeyModifiers.Shift;

    /// <summary>Opens the capture chooser (region / this monitor / all monitors).</summary>
    public static Hotkey DefaultCapture { get; } = new(AltShift, "Q");

    /// <summary>Parse a human string such as <c>"Alt+Shift+Q"</c>. Order-insensitive; throws on garbage.</summary>
    public static Hotkey Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("Hotkey string is empty.");

        var mods = HotkeyModifiers.None;
        string? key = null;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "alt": mods |= HotkeyModifiers.Alt; break;
                case "ctrl": case "control": mods |= HotkeyModifiers.Control; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "win": case "super": case "meta": mods |= HotkeyModifiers.Win; break;
                default:
                    if (key is not null)
                        throw new FormatException($"Hotkey '{text}' has more than one main key.");
                    key = raw.ToUpperInvariant();
                    break;
            }
        }

        if (key is null)
            throw new FormatException($"Hotkey '{text}' has no main key.");
        if (!IsSupportedKey(key))
            throw new FormatException($"Hotkey key '{key}' is not supported.");

        return new Hotkey(mods, key);
    }

    /// <summary>The Win32 <c>fsModifiers</c> mask, with <c>MOD_NOREPEAT</c> so held keys fire once.</summary>
    public uint ToWin32Modifiers() => (uint)Modifiers | MOD_NOREPEAT;

    /// <summary>The Win32 virtual-key code for the main key.</summary>
    public uint ToVirtualKey()
    {
        if (Key.Length == 1)
        {
            var c = Key[0];
            if (c is >= 'A' and <= 'Z') return c;             // VK_A..VK_Z == 'A'..'Z'
            if (c is >= '0' and <= '9') return c;             // VK_0..VK_9 == '0'..'9'
        }
        else if (Key.Length is 2 or 3 && (Key[0] is 'F' or 'f')
                 && int.TryParse(Key.AsSpan(1), out var n) && n is >= 1 and <= 24)
        {
            return (uint)(0x70 + (n - 1));                    // VK_F1 == 0x70
        }

        throw new InvalidOperationException($"No virtual-key mapping for '{Key}'.");
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(Key);
        return string.Join("+", parts);
    }

    private static bool IsSupportedKey(string key)
    {
        if (key.Length == 1)
            return key[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9');
        return key.Length is 2 or 3 && key[0] == 'F'
            && int.TryParse(key.AsSpan(1), out var n) && n is >= 1 and <= 24;
    }
}
