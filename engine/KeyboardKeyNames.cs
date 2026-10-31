using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JoyPro.Engine
{
    // Keyboard key names. JoyPro 1 and the registry games use DirectInput's names (LeftAlt, LeftControl, Oem102, D1,
    // NumberPad1, Return, ...); DCS uses its own (LAlt, LCtrl, OEM102, 1, Num1, Enter, ...). Both name the same key:
    // - ToDcs: DCS's spelling (the form a DCS import stores; unknown names stay as they are, letters and F keys are the
    //   same in both).
    // - ToDirectInput: DirectInput's spelling (for exporters that need it, e.g. IL-2's IL2DX table).
    // - SameKey: two names of one key, whatever spelling.
    // The table is TOOLS\Conversions\DCS2DX.keyboardconversion plus the keys that file lacks (Esc, Scroll, SysRQ).
    internal static class KeyboardKeyNames
    {
        private static readonly string[,] Pairs =
        {
            { "\\", "Backslash" }, { ",", "Comma" }, { ".", "Period" }, { "/", "Slash" }, { ";", "Semicolon" },
            { "[", "LeftBracket" }, { "]", "RightBracket" }, { "=", "Equals" }, { "-", "Minus" }, { "'", "Apostrophe" },
            { "0", "D0" }, { "1", "D1" }, { "2", "D2" }, { "3", "D3" }, { "4", "D4" }, { "5", "D5" }, { "6", "D6" },
            { "7", "D7" }, { "8", "D8" }, { "9", "D9" },
            { "Up", "UpArrow" }, { "Down", "DownArrow" }, { "Left", "LeftArrow" }, { "Right", "RightArrow" },
            { "Num*", "NumberPadStar" }, { "Num.", "NumberPadPeriod" }, { "Num/", "NumberPadSlash" },
            { "Num+", "NumberPadPlus" }, { "Num-", "NumberPadMinus" },
            { "Num0", "NumberPad0" }, { "Num1", "NumberPad1" }, { "Num2", "NumberPad2" }, { "Num3", "NumberPad3" },
            { "Num4", "NumberPad4" }, { "Num5", "NumberPad5" }, { "Num6", "NumberPad6" }, { "Num7", "NumberPad7" },
            { "Num8", "NumberPad8" }, { "Num9", "NumberPad9" }, { "NumEnter", "NumberPadEnter" }, { "NumLock", "NumberLock" },
            { "LWin", "LeftWindowsKey" }, { "RWin", "RightWindowsKey" },
            { "LCtrl", "LeftControl" }, { "RCtrl", "RightControl" }, { "LAlt", "LeftAlt" }, { "RAlt", "RightAlt" },
            { "LShift", "LeftShift" }, { "RShift", "RightShift" },
            { "`", "Grave" }, { "Enter", "Return" }, { "Back", "Backspace" }, { "OEM102", "Oem102" },
            { "Esc", "Escape" }, { "Scroll", "ScrollLock" }, { "SysRQ", "PrintScreen" }
        };

        // Other spellings seen in files (SharpDX's enum aliases, DCS's own variants) -> DCS's spelling.
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "LeftCtrl", "LCtrl" }, { "RightCtrl", "RCtrl" }, { "LControl", "LCtrl" }, { "RControl", "RCtrl" },
            { "LMenu", "LAlt" }, { "RMenu", "RAlt" }, { "LeftWin", "LWin" }, { "RightWin", "RWin" },
            { "Capital", "CapsLock" }, { "Numlock", "NumLock" }, { "Snapshot", "SysRQ" }, { "Print", "SysRQ" },
            { "Oem_102", "OEM102" }, { "BackSpace", "Back" }, { "NumPadEnter", "NumEnter" }
        };

        // DCS's own modifier names of the keyboard (modifiers.lua of a fresh profile): a merge keeps a name the user gave
        // before one of these.
        private static readonly HashSet<string> DcsBuiltInModifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LAlt", "RAlt", "LCtrl", "RCtrl", "LShift", "RShift", "LWin", "RWin"
        };

        private static readonly Dictionary<string, string> DxByDcs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> DcsByDx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static KeyboardKeyNames()
        {
            for (int i = 0; i < Pairs.GetLength(0); i++)
            {
                DxByDcs[Pairs[i, 0]] = Pairs[i, 1];
                DcsByDx[Pairs[i, 1]] = Pairs[i, 0];
            }
        }

        public static bool IsKeyboard(string device)
        {
            return string.Equals((device ?? string.Empty).Trim(), "Keyboard", StringComparison.OrdinalIgnoreCase);
        }

        public static string ToDcs(string key)
        {
            string value = (key ?? string.Empty).Trim();
            if (value.Length == 0)
                return value;
            string mapped;
            if (DxByDcs.ContainsKey(value))
                return Canonical(DxByDcs, value);
            if (DcsByDx.TryGetValue(value, out mapped))
                return mapped;
            if (Aliases.TryGetValue(value, out mapped))
                return mapped;
            // Letters and F keys: the same in both, DCS writes them upper case.
            if (value.Length == 1 && char.IsLetter(value[0]))
                return value.ToUpperInvariant();
            int f;
            if ((value[0] == 'f' || value[0] == 'F') && int.TryParse(value.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out f))
                return "F" + f.ToString(CultureInfo.InvariantCulture);
            return value;
        }

        public static string ToDirectInput(string key)
        {
            string dcs = ToDcs(key);
            string mapped;
            return DxByDcs.TryGetValue(dcs, out mapped) ? mapped : dcs;
        }

        public static bool SameKey(string left, string right)
        {
            return string.Equals(ToDcs(left), ToDcs(right), StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsDcsBuiltInModifier(string name)
        {
            return DcsBuiltInModifiers.Contains((name ?? string.Empty).Trim());
        }

        // The names importers give a modifier by themselves (IL-2 and the registry games name it after DirectInput's
        // key: LeftAlt, LeftControl, ...).
        private static readonly HashSet<string> DirectInputBuiltInModifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LeftAlt", "RightAlt", "LeftControl", "RightControl", "LeftShift", "RightShift", "LeftWindowsKey", "RightWindowsKey"
        };

        // Which name a merge keeps: 0 = a name the user gave, 1 = DCS's built-in name, 2 = an importer's DirectInput name.
        public static int ModifierNameRank(string name)
        {
            string value = (name ?? string.Empty).Trim();
            if (DcsBuiltInModifiers.Contains(value))
                return 1;
            return DirectInputBuiltInModifiers.Contains(value) ? 2 : 0;
        }

        // A reformer "name§device§key§switch" with a keyboard key in DCS's spelling (other devices unchanged).
        public static string NormalizeReformer(string reformer)
        {
            Modifier modifier = Modifier.ReformerToMod(reformer);
            if (modifier == null || !IsKeyboard(modifier.device))
                return reformer;
            string key = ToDcs(modifier.key);
            if (string.Equals(key, modifier.key, StringComparison.Ordinal))
                return reformer;
            modifier.key = key;
            return modifier.toReformerString();
        }

        // The table's own spelling of a DCS name given in any case ("lalt" -> "LAlt").
        private static string Canonical(Dictionary<string, string> table, string value)
        {
            foreach (string key in table.Keys)
            {
                if (string.Equals(key, value, StringComparison.OrdinalIgnoreCase))
                    return key;
            }
            return value;
        }
    }

    // Modifiers that name the same device and key under several names (JoyPro 1's "KeyboardLeftAlt" on LeftAlt next to
    // DCS's "LAlt" on LAlt) become one: the user's own name wins over DCS's built-in names, those over the DirectInput
    // names importers give (LeftAlt); then the name more binds use, then the first in order. Every bind keeps its modifier under that name. The key keeps the spelling its game
    // needs: binds of a relation with DCS actions get DCS's spelling, the others keep theirs.
    internal static class KeyboardModifierMerge
    {
        internal sealed class MergeResult
        {
            public string Name = string.Empty;
            public string Device = string.Empty;
            public string Key = string.Empty;
            public List<string> Merged = new List<string>();
            public int BindsChanged;

            public string Describe()
            {
                return string.Join(" + ", Merged) + " -> " + Name + " (" + Device + " " + Key + ")";
            }
        }

        public static List<MergeResult> MergeDuplicates()
        {
            List<MergeResult> results = new List<MergeResult>();
            Dictionary<string, Bind> binds = InternalDataManagement.AllBinds;
            if (binds == null || binds.Count == 0)
                return results;

            // device|key -> name -> number of binds that use it.
            Dictionary<string, Dictionary<string, int>> byInput = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> deviceOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Bind bind in binds.Values)
            {
                if (bind == null || bind.AllReformers == null)
                    continue;
                foreach (string reformer in bind.AllReformers)
                {
                    Modifier modifier = Modifier.ReformerToMod(reformer);
                    if (modifier == null || string.IsNullOrWhiteSpace(modifier.name) || string.IsNullOrWhiteSpace(modifier.key))
                        continue;
                    string input = InputKey(modifier);
                    Dictionary<string, int> names;
                    if (!byInput.TryGetValue(input, out names))
                    {
                        names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        byInput[input] = names;
                        deviceOf[input] = modifier.device.Trim();
                    }
                    int count;
                    names.TryGetValue(modifier.name.Trim(), out count);
                    names[modifier.name.Trim()] = count + 1;
                }
            }

            foreach (KeyValuePair<string, Dictionary<string, int>> input in byInput.OrderBy(i => i.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (input.Value.Count < 2)
                    continue;
                string survivor = input.Value
                    .OrderBy(n => KeyboardKeyNames.ModifierNameRank(n.Key))
                    .ThenByDescending(n => n.Value)
                    .ThenBy(n => n.Key, StringComparer.Ordinal)
                    .First().Key;
                MergeResult result = new MergeResult
                {
                    Name = survivor,
                    Device = deviceOf[input.Key],
                    Key = input.Key.Substring(input.Key.IndexOf('|') + 1),
                    Merged = input.Value.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList()
                };
                HashSet<string> mergedNames = new HashSet<string>(input.Value.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (Bind bind in binds.Values)
                {
                    if (bind == null || bind.AllReformers == null || bind.AllReformers.Count == 0)
                        continue;
                    bool dcs = RelationHasDcs(bind.Rl);
                    bool changed = false;
                    List<string> rewritten = new List<string>();
                    foreach (string reformer in bind.AllReformers)
                    {
                        Modifier modifier = Modifier.ReformerToMod(reformer);
                        if (modifier == null || !mergedNames.Contains((modifier.name ?? string.Empty).Trim()) ||
                            !string.Equals(InputKey(modifier), input.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            rewritten.Add(reformer);
                            continue;
                        }
                        Modifier merged = modifier.Copy();
                        merged.name = survivor;
                        if (dcs && KeyboardKeyNames.IsKeyboard(merged.device))
                            merged.key = KeyboardKeyNames.ToDcs(merged.key);
                        string text = merged.toReformerString();
                        if (!string.Equals(text, reformer, StringComparison.Ordinal))
                            changed = true;
                        rewritten.Add(text);
                    }
                    List<string> distinct = DistinctByModifier(rewritten);
                    if (!changed && distinct.Count == bind.AllReformers.Count)
                        continue;
                    bind.AllReformers = distinct;
                    result.BindsChanged++;
                }
                results.Add(result);
            }

            if (results.Count > 0)
                InternalDataManagement.ResyncBindsToMods();
            return results;
        }

        // A bind's modifiers without two of one name (the merge can make "LAlt" twice).
        private static List<string> DistinctByModifier(List<string> reformers)
        {
            List<string> output = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string reformer in reformers)
            {
                Modifier modifier = Modifier.ReformerToMod(reformer);
                string key = modifier == null ? reformer : (modifier.name ?? string.Empty).Trim() + "|" + InputKey(modifier);
                if (seen.Add(key))
                    output.Add(reformer);
            }
            return output;
        }

        private static string InputKey(Modifier modifier)
        {
            string device = (modifier.device ?? string.Empty).Trim();
            string key = KeyboardKeyNames.IsKeyboard(device) ? KeyboardKeyNames.ToDcs(modifier.key) : (modifier.key ?? string.Empty).Trim();
            return device.ToLowerInvariant() + "|" + key;
        }

        private static bool RelationHasDcs(Relation relation)
        {
            if (relation == null || relation.NODES == null || relation.NODES.Count == 0)
                return true;
            return relation.NODES.Any(n => n != null && (string.IsNullOrWhiteSpace(n.Game) || string.Equals(n.Game, "DCS", StringComparison.OrdinalIgnoreCase)));
        }
    }
}
