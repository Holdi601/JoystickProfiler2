using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JoyPro.Engine.Games
{
    // What IL-2 Sturmovik: 1946 and Cliffs of Dover share: their key names (one Java/C# key table lineage), the axis
    // names AXE_X .. AXE_V, hat directions as Pov<angle>, and JoyPro action ids "<section>.<command>" (the section is the
    // "[HotKey <section>]" the command lives in). See docs/games/il2-1946.md and il2-cliffs-of-dover.md.
    internal static class Il2LegacyInput
    {
        // DIJOYSTATE2 slot order: X Y Z Rx Ry Rz Slider1 Slider2 = the games' X Y Z RX RY RZ U V.
        public static readonly string[] AxisNames = { "X", "Y", "Z", "RX", "RY", "RZ", "U", "V" };

        public static int AxisIndex(string name)
        {
            return Array.FindIndex(AxisNames, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        }

        // "pilot.Gear" -> ("pilot", "Gear").
        public static bool TrySplitId(string id, out string section, out string command)
        {
            section = string.Empty;
            command = string.Empty;
            string value = (id ?? string.Empty).Trim();
            int dot = value.IndexOf('.');
            if (dot <= 0 || dot == value.Length - 1)
                return false;
            section = value.Substring(0, dot);
            command = value.Substring(dot + 1);
            return true;
        }

        public static string Id(string section, string command)
        {
            return section + "." + command;
        }

        // Pov0, Pov45 .. Pov315 -> direction 0..7 (JoyPro: clockwise from up).
        public static bool TryParsePovAngle(string token, out int direction)
        {
            direction = -1;
            int angle;
            if (token == null || !token.StartsWith("Pov", StringComparison.Ordinal) ||
                !int.TryParse(token.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out angle) || angle % 45 != 0 || angle < 0 || angle > 315)
                return false;
            direction = angle / 45;
            return true;
        }

        public static string PovAngle(int direction)
        {
            return "Pov" + (direction * 45).ToString(CultureInfo.InvariantCulture);
        }

        // Game key name <-> JoyPro key name (DirectInput Key enum names). Letters and F-keys are the same in both.
        private static readonly Dictionary<string, string> GameToJoyPro = BuildKeys();
        private static readonly Dictionary<string, string> JoyProToGame = GameToJoyPro
            .GroupBy(e => e.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, string> BuildKeys()
        {
            Dictionary<string, string> keys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "Equals", "Equals" }, { "Underscore", "Minus" }, { "OpenBracket", "LeftBracket" }, { "CloseBracket", "RightBracket" },
                { "Quote", "Apostrophe" }, { "BackQuote", "Grave" }, { "Slash", "Slash" }, { "BackSlash", "Backslash" },
                { "Comma", "Comma" }, { "Period", "Period" }, { "Semicolon", "Semicolon" }, { "Enter", "Return" },
                { "Backspace", "Backspace" }, { "Escape", "Escape" }, { "Tab", "Tab" }, { "Space", "Space" }, { "Pause", "Pause" },
                { "PrintScreen", "PrintScreen" }, { "Delete", "Delete" }, { "Insert", "Insert" }, { "Home", "Home" }, { "End", "End" },
                { "PageUp", "PageUp" }, { "PageDown", "PageDown" }, { "Up", "UpArrow" }, { "Down", "DownArrow" }, { "Left", "LeftArrow" },
                { "Right", "RightArrow" }, { "NumPad+", "NumberPadPlus" }, { "NumPad-", "NumberPadMinus" }, { "NumPad*", "NumberPadStar" },
                { "NumPad/", "NumberPadSlash" }, { "NumPad.", "NumberPadPeriod" }, { "CapsLock", "CapsLock" }, { "ScrollLock", "ScrollLock" },
                { "NumLock", "NumberLock" }
            };
            for (int i = 0; i <= 9; i++)
            {
                string digit = i.ToString(CultureInfo.InvariantCulture);
                keys[digit] = "D" + digit;
                keys["NumPad-" + digit] = "NumberPad" + digit;
            }
            for (char c = 'A'; c <= 'Z'; c++)
                keys[c.ToString()] = c.ToString();
            for (int i = 1; i <= 12; i++)
                keys["F" + i.ToString(CultureInfo.InvariantCulture)] = "F" + i.ToString(CultureInfo.InvariantCulture);
            return keys;
        }

        // Modifier words of the files: IL-2 1946 writes Ctrl, Cliffs of Dover Control; both write Shift and Alt.
        public static string ModifierToJoyPro(string token)
        {
            switch (token)
            {
                case "Ctrl":
                case "Control":
                    return "LeftControl";
                case "Shift":
                    return "LeftShift";
                case "Alt":
                    return "LeftAlt";
                default:
                    return null;
            }
        }

        public static string ModifierFromJoyPro(string key, string ctrlWord)
        {
            switch ((key ?? string.Empty).Trim())
            {
                case "LeftControl":
                case "RightControl":
                    return ctrlWord;
                case "LeftShift":
                case "RightShift":
                    return "Shift";
                case "LeftAlt":
                case "RightAlt":
                    return "Alt";
                default:
                    return null;
            }
        }

        public static string KeyToJoyPro(string gameKey)
        {
            string name;
            return GameToJoyPro.TryGetValue(gameKey ?? string.Empty, out name) ? name : null;
        }

        public static string KeyFromJoyPro(string joyProKey)
        {
            string name;
            return JoyProToGame.TryGetValue((joyProKey ?? string.Empty).Trim(), out name) ? name : null;
        }

        public static string KeyboardReformer(string joyProKey)
        {
            return joyProKey + "§Keyboard§" + joyProKey + "§False";
        }

        // A JoyPro reformer "name§device§key§sw" -> device and key.
        public static bool TrySplitReformer(string reformer, out string device, out string key)
        {
            device = string.Empty;
            key = string.Empty;
            string[] parts = (reformer ?? string.Empty).Split('§');
            if (parts.Length < 3)
                return false;
            device = parts[1].Trim();
            key = parts[2].Trim();
            return device.Length > 0 && key.Length > 0;
        }

        public static string Humanize(string command)
        {
            string text = (command ?? string.Empty).Replace('_', ' ');
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (i > 0 && char.IsUpper(text[i]) && char.IsLower(text[i - 1]))
                    sb.Append(' ');
                sb.Append(text[i]);
            }
            string result = sb.ToString().Trim();
            return result.Length == 0 ? command : char.ToUpperInvariant(result[0]) + result.Substring(1);
        }
    }
}
