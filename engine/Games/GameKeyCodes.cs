using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.Engine.Games
{
    // JoyPro keyboard key names (DirectInput Key enum names), their DIK scan codes (War Thunder, X4, Arma) and their
    // Unreal Engine key names (Hell Let Loose: Vietnam).
    internal static class GameKeyCodes
    {
        private static readonly Dictionary<string, int> DikByName = BuildDikTable();
        private static readonly Dictionary<int, string> NameByDik = DikByName
            .GroupBy(e => e.Value)
            .ToDictionary(g => g.Key, g => g.First().Key);

        public static bool TryGetDik(string keyName, out int dik)
        {
            return DikByName.TryGetValue((keyName ?? string.Empty).Trim(), out dik);
        }

        public static string NameFromDik(int dik)
        {
            string name;
            return NameByDik.TryGetValue(dik, out name) ? name : string.Empty;
        }

        // Unreal Engine FKey names (EKeys) for JoyPro key names; letters and F-keys have the same name in both.
        private static readonly Dictionary<string, string> UnrealByName = BuildUnrealTable();
        private static readonly Dictionary<string, string> NameByUnreal = UnrealByName
            .GroupBy(e => e.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

        public static bool TryGetUnrealKey(string keyName, out string unrealKey)
        {
            return UnrealByName.TryGetValue((keyName ?? string.Empty).Trim(), out unrealKey);
        }

        public static string NameFromUnrealKey(string unrealKey)
        {
            string name;
            return NameByUnreal.TryGetValue((unrealKey ?? string.Empty).Trim(), out name) ? name : string.Empty;
        }

        private static Dictionary<string, string> BuildUnrealTable()
        {
            Dictionary<string, string> table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Escape", "Escape" }, { "Minus", "Hyphen" }, { "Equals", "Equals" }, { "Backspace", "BackSpace" },
                { "Tab", "Tab" }, { "LeftBracket", "LeftBracket" }, { "RightBracket", "RightBracket" }, { "Return", "Enter" },
                { "LeftControl", "LeftControl" }, { "RightControl", "RightControl" }, { "Semicolon", "Semicolon" },
                { "Apostrophe", "Apostrophe" }, { "Grave", "Tilde" }, { "LeftShift", "LeftShift" }, { "RightShift", "RightShift" },
                { "Backslash", "Backslash" }, { "Comma", "Comma" }, { "Period", "Period" }, { "Slash", "Slash" },
                { "LeftAlt", "LeftAlt" }, { "RightAlt", "RightAlt" }, { "Space", "SpaceBar" }, { "CapsLock", "CapsLock" },
                { "NumberLock", "NumLock" }, { "ScrollLock", "ScrollLock" }, { "Pause", "Pause" },
                { "NumberPadStar", "Multiply" }, { "NumberPadPlus", "Add" }, { "NumberPadMinus", "Subtract" },
                { "NumberPadSlash", "Divide" }, { "NumberPadPeriod", "Decimal" },
                { "Home", "Home" }, { "End", "End" }, { "PageUp", "PageUp" }, { "PageDown", "PageDown" },
                { "Insert", "Insert" }, { "Delete", "Delete" }, { "UpArrow", "Up" }, { "DownArrow", "Down" },
                { "LeftArrow", "Left" }, { "RightArrow", "Right" }
            };
            string[] digits = { "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine" };
            for (int i = 0; i < 10; i++)
            {
                table["D" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = digits[i];
                table["NumberPad" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = "NumPad" + digits[i];
            }
            for (char c = 'A'; c <= 'Z'; c++)
                table[c.ToString()] = c.ToString();
            for (int i = 1; i <= 12; i++)
                table["F" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = "F" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return table;
        }

        private static Dictionary<string, int> BuildDikTable()
        {
            Dictionary<string, int> table = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "Escape", 0x01 }, { "D1", 0x02 }, { "D2", 0x03 }, { "D3", 0x04 }, { "D4", 0x05 }, { "D5", 0x06 },
                { "D6", 0x07 }, { "D7", 0x08 }, { "D8", 0x09 }, { "D9", 0x0A }, { "D0", 0x0B }, { "Minus", 0x0C },
                { "Equals", 0x0D }, { "Backspace", 0x0E }, { "Tab", 0x0F }, { "Q", 0x10 }, { "W", 0x11 }, { "E", 0x12 },
                { "R", 0x13 }, { "T", 0x14 }, { "Y", 0x15 }, { "U", 0x16 }, { "I", 0x17 }, { "O", 0x18 }, { "P", 0x19 },
                { "LeftBracket", 0x1A }, { "RightBracket", 0x1B }, { "Return", 0x1C }, { "LeftControl", 0x1D },
                { "A", 0x1E }, { "S", 0x1F }, { "D", 0x20 }, { "F", 0x21 }, { "G", 0x22 }, { "H", 0x23 }, { "J", 0x24 },
                { "K", 0x25 }, { "L", 0x26 }, { "Semicolon", 0x27 }, { "Apostrophe", 0x28 }, { "Grave", 0x29 },
                { "LeftShift", 0x2A }, { "Backslash", 0x2B }, { "Z", 0x2C }, { "X", 0x2D }, { "C", 0x2E }, { "V", 0x2F },
                { "B", 0x30 }, { "N", 0x31 }, { "M", 0x32 }, { "Comma", 0x33 }, { "Period", 0x34 }, { "Slash", 0x35 },
                { "RightShift", 0x36 }, { "NumberPadStar", 0x37 }, { "LeftAlt", 0x38 }, { "Space", 0x39 },
                { "CapsLock", 0x3A }, { "F1", 0x3B }, { "F2", 0x3C }, { "F3", 0x3D }, { "F4", 0x3E }, { "F5", 0x3F },
                { "F6", 0x40 }, { "F7", 0x41 }, { "F8", 0x42 }, { "F9", 0x43 }, { "F10", 0x44 }, { "NumberLock", 0x45 },
                { "ScrollLock", 0x46 }, { "NumberPad7", 0x47 }, { "NumberPad8", 0x48 }, { "NumberPad9", 0x49 },
                { "NumberPadMinus", 0x4A }, { "NumberPad4", 0x4B }, { "NumberPad5", 0x4C }, { "NumberPad6", 0x4D },
                { "NumberPadPlus", 0x4E }, { "NumberPad1", 0x4F }, { "NumberPad2", 0x50 }, { "NumberPad3", 0x51 },
                { "NumberPad0", 0x52 }, { "NumberPadPeriod", 0x53 }, { "Oem102", 0x56 }, { "F11", 0x57 }, { "F12", 0x58 },
                { "F13", 0x64 }, { "F14", 0x65 }, { "F15", 0x66 }, { "NumberPadEnter", 0x9C }, { "RightControl", 0x9D },
                { "NumberPadSlash", 0xB5 }, { "PrintScreen", 0xB7 }, { "RightAlt", 0xB8 }, { "Pause", 0xC5 },
                { "Home", 0xC7 }, { "UpArrow", 0xC8 }, { "PageUp", 0xC9 }, { "LeftArrow", 0xCB }, { "RightArrow", 0xCD },
                { "End", 0xCF }, { "DownArrow", 0xD0 }, { "PageDown", 0xD1 }, { "Insert", 0xD2 }, { "Delete", 0xD3 },
                { "LeftWindowsKey", 0xDB }, { "RightWindowsKey", 0xDC }
            };
            return table;
        }
    }
}
