using Windows.System;
namespace m3nuxa.App;
internal static class HotkeyParser
{
    public static bool IsModifier(uint key) => key is 0x10 or 0x11 or 0x12 or 0x5b or 0x5c or >= 0xa0 and <= 0xa5;
    public static uint Modifier(uint key) => key switch
    {
        0x10 or 0xa0 or 0xa1 => 4, 0x11 or 0xa2 or 0xa3 => 2,
        0x12 or 0xa4 or 0xa5 => 1, 0x5b or 0x5c => 8, _ => 0
    };
    public static string Format(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        if ((modifiers & 8) != 0) parts.Add("Win");
        if (key != 0) parts.Add(key switch
        {
            >= 0x30 and <= 0x39 => ((char)key).ToString(),
            >= 0x60 and <= 0x69 => "NumPad" + (key - 0x60),
            0xba => "Oem1", 0xbb => "OemPlus", 0xbc => "OemComma", 0xbd => "OemMinus",
            0xbe => "OemPeriod", 0xbf => "Oem2", 0xc0 => "Oem3", 0xdb => "Oem4",
            0xdc => "Oem5", 0xdd => "Oem6", 0xde => "Oem7", 0xe2 => "Oem102",
            _ => ((VirtualKey)key).ToString()
        });
        return string.Join("+", parts);
    }
    public static bool TryKey(string text, out VirtualKey key)
    {
        uint oem = text.ToUpperInvariant() switch { "OEM1" or "OEMSEMICOLON" => 0xba, "OEMPLUS" => 0xbb, "OEMCOMMA" => 0xbc, "OEMMINUS" => 0xbd, "OEMPERIOD" => 0xbe, "OEM2" or "OEMQUESTION" => 0xbf, "OEM3" or "OEMTILDE" => 0xc0, "OEM4" or "OEMOPENBRACKETS" => 0xdb, "OEM5" or "OEMPIPE" => 0xdc, "OEM6" or "OEMCLOSEBRACKETS" => 0xdd, "OEM7" or "OEMQUOTES" => 0xde, "OEM102" or "OEMBACKSLASH" => 0xe2, _ => 0 };
        if (oem != 0) { key = (VirtualKey)oem; return true; }
        text = text.ToUpperInvariant() switch
        {
            "ESC" => "Escape", "PGUP" => "PageUp", "PGDN" => "PageDown",
            "RETURN" => "Enter", "BACK" or "BACKSPACE" => "Back",
            "PRIOR" => "PageUp", "NEXT" => "PageDown", "CAPITAL" => "CapitalLock",
            var s when s.Length == 2 && s[0] == 'D' && char.IsAsciiDigit(s[1]) => "Number" + s[1],
            var s when s.Length == 1 && char.IsAsciiDigit(s[0]) => "Number" + s,
            var s when s.StartsWith("NUMPAD") => "NumberPad" + s[6..],
            _ => text
        };
        return Enum.TryParse(text, true, out key) && Enum.IsDefined(key) && key != VirtualKey.None;
    }
    public static bool TryParse(string text, out uint modifiers, out uint key)
    {
        modifiers = key = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            uint modifier = part.ToLowerInvariant() switch { "ctrl" or "control" => 2, "alt" => 1, "shift" => 4, "win" => 8, _ => 0 };
            if (modifier != 0) { if (key != 0 || (modifiers & modifier) != 0) return false; modifiers |= modifier; }
            else { if (key != 0 || !TryKey(part, out var parsed) || IsModifier((uint)parsed)) return false; key = (uint)parsed; }
        }
        return key != 0;
    }
}

