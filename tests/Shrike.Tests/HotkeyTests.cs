using Shrike.Core.Hotkeys;

namespace Shrike.Tests;

public class HotkeyTests
{
    [Fact]
    public void Default_region_hotkey_is_alt_shift_q()
    {
        var hk = Hotkey.DefaultRegion;
        Assert.Equal(HotkeyModifiers.Alt | HotkeyModifiers.Shift, hk.Modifiers);
        Assert.Equal("Q", hk.Key);
        Assert.Equal("Alt+Shift+Q", hk.ToString());
    }

    [Theory]
    [InlineData("Alt+Shift+Q")]
    [InlineData("shift+alt+q")]      // order- and case-insensitive
    [InlineData(" Alt + Shift + Q ")]
    public void Parse_roundtrips_to_canonical_string(string input)
    {
        var hk = Hotkey.Parse(input);
        Assert.Equal("Alt+Shift+Q", hk.ToString());
    }

    [Fact]
    public void Parse_maps_ctrl_aliases()
    {
        Assert.Equal(HotkeyModifiers.Control, Hotkey.Parse("Ctrl+A").Modifiers);
        Assert.Equal(HotkeyModifiers.Control, Hotkey.Parse("Control+A").Modifiers);
    }

    [Fact]
    public void ToWin32Modifiers_matches_MOD_constants_with_norepeat()
    {
        // MOD_ALT(1) | MOD_SHIFT(4) | MOD_NOREPEAT(0x4000)
        Assert.Equal(0x4000u | 0x1u | 0x4u, Hotkey.DefaultRegion.ToWin32Modifiers());
    }

    [Theory]
    [InlineData("Q", 0x51u)]
    [InlineData("A", 0x41u)]
    [InlineData("Z", 0x5Au)]
    [InlineData("0", 0x30u)]
    [InlineData("9", 0x39u)]
    public void Letter_and_digit_keys_map_to_virtual_keys(string key, uint expected)
    {
        var hk = new Hotkey(HotkeyModifiers.Alt, key);
        Assert.Equal(expected, hk.ToVirtualKey());
    }

    [Theory]
    [InlineData("F1", 0x70u)]
    [InlineData("F12", 0x7Bu)]
    public void Function_keys_map_to_virtual_keys(string key, uint expected)
    {
        Assert.Equal(expected, Hotkey.Parse("Alt+" + key).ToVirtualKey());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Alt")]                 // no main key
    [InlineData("Alt+Q+W")]             // two main keys
    [InlineData("Alt+;")]               // unsupported key
    public void Parse_rejects_bad_input(string input)
    {
        Assert.Throws<FormatException>(() => Hotkey.Parse(input));
    }
}
