namespace Voxta.Modules.KeyboardShortcuts;

internal static class WindowsShortcutKeys
{
    private static readonly Dictionary<string, ushort> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = 0xA2, ["Control"] = 0xA2, ["Strg"] = 0xA2,
        ["Alt"] = 0xA4, ["Shift"] = 0xA0, ["Win"] = 0x5B,
    };

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Pause"] = 0x13, ["PauseBreak"] = 0x13,
        ["Space"] = 0x20, ["PageUp"] = 0x21, ["PgUp"] = 0x21, ["BildAuf"] = 0x21,
        ["PageDown"] = 0x22, ["PgDn"] = 0x22, ["BildAb"] = 0x22,
        ["End"] = 0x23, ["Ende"] = 0x23, ["Home"] = 0x24, ["Pos1"] = 0x24,
        ["Left"] = 0x25, ["ArrowLeft"] = 0x25, ["Up"] = 0x26, ["ArrowUp"] = 0x26,
        ["Right"] = 0x27, ["ArrowRight"] = 0x27, ["Down"] = 0x28, ["ArrowDown"] = 0x28,
        ["PrintScreen"] = 0x2C, ["PrtSc"] = 0x2C, ["Druck"] = 0x2C,
        ["Insert"] = 0x2D, ["Einfg"] = 0x2D, ["Delete"] = 0x2E, ["Entf"] = 0x2E,
        ["NumLock"] = 0x90, ["ScrollLock"] = 0x91, ["Rollen"] = 0x91,
        ["NumpadMultiply"] = 0x6A, ["NumpadAdd"] = 0x6B, ["NumpadSubtract"] = 0x6D,
        ["NumpadDecimal"] = 0x6E, ["NumpadDivide"] = 0x6F,
        ["MediaNextTrack"] = 0xB0, ["MediaPreviousTrack"] = 0xB1,
        ["MediaStop"] = 0xB2, ["MediaPlayPause"] = 0xB3,
    };

    internal static readonly string Pattern =
        $@"^(?:(?:{Alternatives(Modifiers.Keys)})[ \t]*\+[ \t]*){{0,4}}(?:[A-Za-z0-9]|[Ff](?:[1-9]|1[0-9]|2[0-4])|[Nn][Uu][Mm][Pp][Aa][Dd][0-9]|{Alternatives(NamedKeys.Keys)})$";

    internal const string Guide = "Use one main key and optional Ctrl (Strg), Alt, Shift, or Win modifiers, for example Ctrl+Shift+F10.<br />"
        + "Supported keys include A–Z, 0–9, F1–F24, Enter, Space, Tab, Backspace, Esc, Left, Right, Up, Down, "
        + "PageUp/BildAuf, PageDown/BildAb, Home/Pos1, End/Ende, Insert/Einfg, Delete/Entf, "
        + "PrintScreen/Druck, Pause, ScrollLock/Rollen, NumLock, and Numpad0-Numpad9. "
        + "Additional keys: NumpadMultiply, NumpadAdd, NumpadSubtract, NumpadDecimal, NumpadDivide, "
        + "MediaPlayPause, MediaNextTrack, MediaPreviousTrack, and MediaStop.";

    public static bool TryParse(string text, out ushort[] keys)
    {
        keys = [];
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length > 5 || !TryGetMainKey(parts[^1], out var mainKey)) return false;

        var modifiers = new SortedSet<ushort>();
        foreach (var part in parts.AsSpan(0, parts.Length - 1))
        {
            if (!Modifiers.TryGetValue(part, out var modifier) || !modifiers.Add(modifier)) return false;
        }

        keys = [.. modifiers, mainKey];
        return true;
    }

    private static bool TryGetMainKey(string name, out ushort key)
    {
        if (NamedKeys.TryGetValue(name, out key)) return true;
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
        {
            key = char.ToUpperInvariant(name[0]);
            return true;
        }
        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && name[1] is >= '1' and <= '9'
            && (name.Length == 2 || char.IsAsciiDigit(name[2]))
            && int.TryParse(name.AsSpan(1), out var function) && function <= 24)
        {
            key = (ushort)(0x70 + function - 1);
            return true;
        }
        if (name.Length == 7 && name.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(name[^1]))
        {
            key = (ushort)(0x60 + name[^1] - '0');
            return true;
        }

        return false;
    }

    private static string Alternatives(IEnumerable<string> names) => string.Join('|', names.Select(name =>
        string.Concat(name.Select(letter => $"[{char.ToLowerInvariant(letter)}{char.ToUpperInvariant(letter)}]"))));
}
