using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace JoyPro.Engine.Games
{
    // Nuclear Option (Shockfront, Unity + Rewired). The binds are Rewired JoystickMap XML values in the game's Unity
    // PlayerPrefs, i.e. the registry key HKCU\Software\Shockfront\NuclearOption, one value per device model:
    // "RewiredSaveData|playerName=Player0|dataType=ControllerMap|kv=2|controllerMapType=JoystickMap|categoryId=0|layoutId=0|
    // hardwareGuid=<zeros>|hardwareIdentifier=WindowsRawInput<HID product, no spaces><PID><VID>|duplicate=0_h<hash>".
    // The XML stores numeric Rewired action ids; DB\NuclearOption\actions.tsv (refresh-db reads them from the game's
    // resources.assets) maps them to the action names. Binds are global (one set for every aircraft). JoyPro edits only
    // the element maps of the actions it writes (clean / nuke: only "Flight Controls" actions); every other value and
    // every other element map stays (see docs/games/nuclear-option.md). The Rewired logic is shared with Tiny Combat
    // Arena (RewiredMapIntegration).
    // Test hook: a config path ending in .json is a fake registry ({ "value name": "<base64 of the value bytes>" }).
    internal sealed class NuclearOptionIntegration : RewiredMapIntegration
    {
        public const string RegistryKey = @"HKCU\Software\Shockfront\NuclearOption";
        private const string SubKey = @"Software\Shockfront\NuclearOption";
        private const string FlightCategory = "Flight Controls";

        // Rewired action categories of the game (resources.assets, InputManager); refresh-db reads the names too.
        private static readonly Dictionary<int, string> Categories = new Dictionary<int, string>
        {
            { 0, "Default" }, { 1, FlightCategory }, { 2, "Camera Controls" }, { 3, "Gameplay" }, { 4, "Debug" }, { 5, "Chat" }, { 6, "Mission Editor" }
        };

        public override string GameId { get { return "NuclearOption"; } }
        public override string DisplayName { get { return "Nuclear Option"; } }
        public override IReadOnlyList<string> Aliases { get { return new[] { "nuclearoption", "no", "nuclear" }; } }
        public override string DbFolder { get { return "NuclearOption"; } }
        public override bool Experimental { get { return true; } }
        public override bool ExclusiveFlightSim { get { return false; } }
        public override string Notes
        {
            get
            {
                return "Experimental. Close Nuclear Option before exporting (it rewrites its bindings when it saves). Binds are global, one set for " +
                       "every aircraft; hats and keyboard binds are not written yet.";
            }
        }
        public override IReadOnlyList<string> ProcessNames { get { return new[] { "NuclearOption" }; } }

        public override List<string> DiscoverConfigPaths(List<string> warnings)
        {
            return UnityPrefsStore.RegistryKeyExists(SubKey) ? new List<string> { RegistryKey } : new List<string>();
        }

        protected override bool TryOpenStore(string configPath, List<string> errors, out RewiredStore store)
        {
            UnityPrefsStore prefs;
            bool ok = UnityPrefsStore.TryOpen(configPath, SubKey, DisplayName, PlayerName, errors, out prefs);
            store = prefs;
            return ok;
        }

        protected override IReadOnlyDictionary<int, string> DefaultCategories { get { return Categories; } }

        protected override bool IsCleanCategory(string category)
        {
            return string.Equals(category, FlightCategory, StringComparison.OrdinalIgnoreCase);
        }

        protected override RewiredLayout CatalogLayout { get { return RewiredLayout.NuclearOption; } }

        protected override string FindCatalogAssets(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                return null;
            string path = sourcePath.Trim().Trim('"');
            if (File.Exists(path))
                return path;
            foreach (string candidate in new[] { Path.Combine(path, "NuclearOption_Data", "resources.assets"), Path.Combine(path, "resources.assets") })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        protected override void BeforeWrite(RewiredStore store, GameExportRequest request, GameIoResult result)
        {
            if (!request.Bindings.Any(b => string.Equals(b.ActionId, "Throttle", StringComparison.OrdinalIgnoreCase) && b.IsAxis))
                return;
            UnityPrefsStore prefs = store as UnityPrefsStore;
            byte[] value;
            if (prefs != null && prefs.TryGetBytes(UnityPrefsStore.ValueNameOf("ThrottleUseNegative"), out value) && value != null && value.Length >= 4 && BitConverter.ToInt32(value, 0) == 0)
                result.Warnings.Add("'Use Throttle Axis Negative Region' is off in the game's controls: a full throttle axis gives thrust only on its upper half.");
        }
    }

    internal sealed class RewiredActionRecord
    {
        public int Id;
        public string Name = string.Empty;
        public int Type;
        public string DescriptiveName = string.Empty;
        public string PositiveName = string.Empty;
        public string NegativeName = string.Empty;
        public int CategoryId;
        // Only known where the layout has the flag (VTOL VR); true otherwise.
        public bool UserAssignable = true;
    }

    // The Rewired InputManager's serialized data in a Unity .assets file: the actions list (int32 count, then per action
    // int32 id, string name, int32 type (0 axis, 1 button), strings descriptiveName / positiveDescriptiveName /
    // negativeDescriptiveName, then int32 fields), directly followed by the action categories (int32 count, then strings
    // name, descriptiveName, tag, then int32 fields). Unity strings: int32 length + UTF-8 + padding to 4 bytes. The list
    // is found by its first action (id 0). The int32 fields depend on the Rewired version (RewiredLayout).
    internal sealed class RewiredLayout
    {
        public string FirstAction = string.Empty;
        public int ActionInts;
        public int ActionCategoryIndex;
        public int ActionAssignableIndex = -1;
        public int CategoryInts;
        public int CategoryIdIndex;
        // Strings per category before its int32 fields (name, descriptiveName, tag; newer Rewired adds a fourth).
        public int CategoryStrings = 3;
        // The category's name, not its descriptive name, is the catalog's category (Tiny Combat Arena describes them in sentences).
        public bool CategoryByName;

        // Nuclear Option: 6 int32 per action (the last one = category id); categories end with int32, int32 id, int32.
        public static readonly RewiredLayout NuclearOption = new RewiredLayout { FirstAction = "Pitch", ActionInts = 6, ActionCategoryIndex = 5, CategoryInts = 3, CategoryIdIndex = 1 };
        // VTOL VR 1.13 (level0): 3 int32 per action (behavior id, userAssignable, category id); categories end with
        // int32 id, int32 userAssignable.
        // Tiny Combat Arena 0.17 (sharedassets1.assets): 6 int32 per action (index 4 = userAssignable, 5 = category id);
        // categories have four strings, then int32 id, int32 userAssignable.
        public static readonly RewiredLayout TinyCombatArena = new RewiredLayout { FirstAction = "Pitch", ActionInts = 6, ActionCategoryIndex = 5, ActionAssignableIndex = 4, CategoryStrings = 4, CategoryInts = 2, CategoryIdIndex = 0, CategoryByName = true };
        public static readonly RewiredLayout VtolVr = new RewiredLayout { FirstAction = "Rudder", ActionInts = 3, ActionCategoryIndex = 2, ActionAssignableIndex = 1, CategoryInts = 2, CategoryIdIndex = 0 };
    }

    internal static class RewiredInputManagerData
    {
        public static bool TryParse(byte[] data, out List<RewiredActionRecord> actions, out Dictionary<int, string> categories, out string error)
        {
            return TryParse(data, RewiredLayout.NuclearOption, out actions, out categories, out error);
        }

        public static bool TryParse(byte[] data, RewiredLayout layout, out List<RewiredActionRecord> actions, out Dictionary<int, string> categories, out string error)
        {
            actions = null;
            categories = new Dictionary<int, string>();
            error = "the Rewired action list (first action '" + layout.FirstAction + "') was not found.";
            byte[] name = Encoding.UTF8.GetBytes(layout.FirstAction);
            byte[] anchor = BitConverter.GetBytes(name.Length).Concat(name).ToArray();
            for (int at = IndexOf(data, anchor, 8); at >= 0; at = IndexOf(data, anchor, at + 1))
            {
                int start = at - 8;
                if (BitConverter.ToInt32(data, at - 4) != 0)
                    continue;
                int end;
                List<RewiredActionRecord> parsed;
                if (!TryParseActions(data, start, layout, out parsed, out end))
                    continue;
                actions = parsed;
                categories = TryParseCategories(data, start, end, layout);
                error = string.Empty;
                return true;
            }
            return false;
        }

        private static bool TryParseActions(byte[] data, int start, RewiredLayout layout, out List<RewiredActionRecord> actions, out int end)
        {
            actions = new List<RewiredActionRecord>();
            end = start;
            int pos = start;
            int count;
            if (!TryInt(data, ref pos, out count) || count < 1 || count > 10000)
                return false;
            HashSet<int> ids = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                RewiredActionRecord record = new RewiredActionRecord();
                int[] tail = new int[layout.ActionInts];
                if (!TryInt(data, ref pos, out record.Id) || !TryString(data, start, ref pos, out record.Name) || !TryInt(data, ref pos, out record.Type) ||
                    !TryString(data, start, ref pos, out record.DescriptiveName) || !TryString(data, start, ref pos, out record.PositiveName) ||
                    !TryString(data, start, ref pos, out record.NegativeName))
                    return false;
                for (int t = 0; t < tail.Length; t++)
                {
                    if (!TryInt(data, ref pos, out tail[t]))
                        return false;
                }
                record.CategoryId = tail[layout.ActionCategoryIndex];
                if (layout.ActionAssignableIndex >= 0)
                {
                    if (tail[layout.ActionAssignableIndex] != 0 && tail[layout.ActionAssignableIndex] != 1)
                        return false;
                    record.UserAssignable = tail[layout.ActionAssignableIndex] == 1;
                }
                if (record.Id < 0 || !ids.Add(record.Id) || record.Name.Length == 0 || (record.Type != 0 && record.Type != 1) || record.CategoryId < 0 || record.CategoryId > 1000)
                    return false;
                actions.Add(record);
            }
            end = pos;
            return true;
        }

        private static Dictionary<int, string> TryParseCategories(byte[] data, int start, int pos, RewiredLayout layout)
        {
            Dictionary<int, string> categories = new Dictionary<int, string>();
            int count;
            if (!TryInt(data, ref pos, out count) || count < 1 || count > 64)
                return categories;
            for (int i = 0; i < count; i++)
            {
                string name;
                string descriptive;
                string tag;
                if (!TryString(data, start, ref pos, out name) || !TryString(data, start, ref pos, out descriptive) || !TryString(data, start, ref pos, out tag))
                    return new Dictionary<int, string>();
                for (int s = 3; s < layout.CategoryStrings; s++)
                {
                    string extra;
                    if (!TryString(data, start, ref pos, out extra))
                        return new Dictionary<int, string>();
                }
                int[] ints = new int[layout.CategoryInts];
                for (int t = 0; t < ints.Length; t++)
                {
                    if (!TryInt(data, ref pos, out ints[t]))
                        return new Dictionary<int, string>();
                }
                int id = ints[layout.CategoryIdIndex];
                if (name.Length == 0 || id < 0 || categories.ContainsKey(id))
                    return new Dictionary<int, string>();
                categories[id] = !layout.CategoryByName && descriptive.Trim().Length > 0 ? descriptive.Trim() : name;
            }
            return categories;
        }

        private static bool TryInt(byte[] data, ref int pos, out int value)
        {
            value = 0;
            if (pos < 0 || pos + 4 > data.Length)
                return false;
            value = BitConverter.ToInt32(data, pos);
            pos += 4;
            return true;
        }

        // Padding is relative to the list's start (4-byte aligned inside the serialized object).
        private static bool TryString(byte[] data, int start, ref int pos, out string value)
        {
            value = string.Empty;
            int length;
            if (!TryInt(data, ref pos, out length) || length < 0 || length > 1024 || pos + length > data.Length)
                return false;
            try
            {
                value = new UTF8Encoding(false, true).GetString(data, pos, length);
            }
            catch (ArgumentException)
            {
                return false;
            }
            if (value.Any(c => char.IsControl(c)))
                return false;
            pos += length;
            int relative = pos - start;
            pos += (4 - relative % 4) % 4;
            return pos <= data.Length;
        }

        private static int IndexOf(byte[] data, byte[] pattern, int from)
        {
            for (int i = Math.Max(0, from); i <= data.Length - pattern.Length; i++)
            {
                if (data[i] != pattern[0])
                    continue;
                int j = 1;
                while (j < pattern.Length && data[i + j] == pattern[j])
                    j++;
                if (j == pattern.Length)
                    return i;
            }
            return -1;
        }
    }

    // Unity PlayerPrefs of a Rewired game (Nuclear Option, Tiny Combat Arena): its HKCU registry key, or (test hook) a
    // .json file { "value name": "base64" }. Strings (all Rewired data) are REG_BINARY = UTF-8 + one 0x00. Value names
    // are "<key>_h<hash>" with Unity's hash (djb2 with XOR over the UTF-8 key, unsigned 32 bit, decimal).
    internal sealed class UnityPrefsStore : RewiredStore
    {
        private readonly List<string> names = new List<string>();
        private readonly Dictionary<string, byte[]> values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private string fakePath = string.Empty;
        private string location = string.Empty;
        private string subKey = string.Empty;
        private string game = string.Empty;
        private string player = string.Empty;

        public override IEnumerable<string> Names
        {
            get { return names; }
        }

        public static bool RegistryKeyExists(string subKey)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(TestSandbox.RegistrySubKeyForRead(subKey), false))
                return key != null;
        }

        public static uint Hash(string key)
        {
            uint hash = 5381;
            foreach (byte b in Encoding.UTF8.GetBytes(key ?? string.Empty))
                hash = unchecked((hash * 33) ^ b);
            return hash;
        }

        public static string ValueNameOf(string key)
        {
            return key + "_h" + Hash(key).ToString(CultureInfo.InvariantCulture);
        }

        // The PlayerPrefs key of a value name (without "_h<hash>").
        public static string KeyOfValueName(string valueName)
        {
            Match match = Regex.Match(valueName ?? string.Empty, @"^(.*)_h\d+$", RegexOptions.Singleline);
            return match.Success ? match.Groups[1].Value : valueName ?? string.Empty;
        }

        public override string KeyOf(string name)
        {
            return KeyOfValueName(name);
        }

        public override string NameOf(string key)
        {
            return ValueNameOf(key);
        }

        // subKey: the game's key under HKCU (Software\<company>\<product>); game: its name for messages; player: the
        // Rewired player whose controller maps JoyPro may write (backup scope).
        public static bool TryOpen(string configPath, string subKey, string game, string player, List<string> errors, out UnityPrefsStore prefs)
        {
            prefs = new UnityPrefsStore { location = configPath ?? string.Empty, subKey = subKey, game = game, player = player };
            string path = (configPath ?? string.Empty).Trim();
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(path))
                {
                    errors.Add("File not found: " + path);
                    return false;
                }
                Dictionary<string, string> raw;
                try
                {
                    raw = EngineSerialization.FromJson<Dictionary<string, string>>(File.ReadAllText(path, Encoding.UTF8)) ?? new Dictionary<string, string>();
                    prefs.fakePath = path;
                    foreach (KeyValuePair<string, string> value in raw)
                        prefs.Put(value.Key, Convert.FromBase64String(value.Value ?? string.Empty));
                }
                catch (Exception ex)
                {
                    errors.Add(path + " is no fake registry file (value name -> base64): " + ex.Message);
                    return false;
                }
                return true;
            }
            if (!GameConfigLocation.IsRegistryKey(path) || !string.Equals(path.Substring(path.IndexOf('\\') + 1), subKey, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("'" + path + "' is not " + game + "'s registry key (HKCU\\" + subKey + ").");
                return false;
            }
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(TestSandbox.RegistrySubKeyForRead(subKey), false))
            {
                if (key == null)
                {
                    errors.Add(game + " has no settings yet (HKCU\\" + subKey + " is missing): start the game once.");
                    return false;
                }
                foreach (string name in key.GetValueNames())
                {
                    // Strings are REG_BINARY; Unity's ints are REG_DWORD (read as int, or as long when the data has 8 bytes).
                    object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value is byte[])
                        prefs.Put(name, (byte[])value);
                    else if (value is int)
                        prefs.Put(name, BitConverter.GetBytes((int)value));
                    else if (value is long)
                        prefs.Put(name, BitConverter.GetBytes((long)value));
                }
            }
            return true;
        }

        private void Put(string name, byte[] bytes)
        {
            if (!values.ContainsKey(name))
                names.Add(name);
            values[name] = bytes;
        }

        public override bool Contains(string name)
        {
            return values.ContainsKey(name);
        }

        public bool TryGetBytes(string name, out byte[] bytes)
        {
            return values.TryGetValue(name, out bytes);
        }

        public override bool TryGetText(string name, out string text)
        {
            text = string.Empty;
            byte[] bytes;
            if (!values.TryGetValue(name, out bytes) || bytes == null)
                return false;
            int length = bytes.Length;
            while (length > 0 && bytes[length - 1] == 0)
                length--;
            text = Encoding.UTF8.GetString(bytes, 0, length);
            return true;
        }

        public static byte[] Encode(string text)
        {
            byte[] utf8 = new UTF8Encoding(false).GetBytes(text ?? string.Empty);
            byte[] bytes = new byte[utf8.Length + 1];
            Buffer.BlockCopy(utf8, 0, bytes, 0, utf8.Length);
            return bytes;
        }

        // Before the first write: the fake file to <file>.jpbak and the backup store; the registry key's Rewired controller
        // maps (the values JoyPro may change or create) as a snapshot in the backup store, which "engine backups restore"
        // writes back. Returns where the backup is.
        public override string Backup(string gameId, List<string> warnings)
        {
            if (fakePath.Length > 0)
            {
                GameExportSafety.BackupBeforeOverwrite(fakePath, warnings);
                return fakePath + GameExportSafety.BackupExtension;
            }
            return GameBackups.ProtectRegistry(gameId, "HKCU\\" + subKey, RewiredMapIntegration.OwnedPrefix(player), warnings);
        }

        // Writes the changed values (UTF-8 + 0x00, REG_BINARY); nothing else of the key changes.
        public override bool Write(Dictionary<string, string> changes, out string error)
        {
            error = string.Empty;
            try
            {
                if (fakePath.Length > 0)
                {
                    foreach (KeyValuePair<string, string> change in changes)
                        Put(change.Key, Encode(change.Value));
                    Dictionary<string, string> raw = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (string name in names)
                        raw[name] = Convert.ToBase64String(values[name]);
                    string temp = fakePath + ".jptmp";
                    File.WriteAllText(temp, EngineSerialization.ToJson(raw), new UTF8Encoding(false));
                    SafeFileIo.CopyOverAndDelete(temp, fakePath);
                    return true;
                }
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(TestSandbox.RegistrySubKeyForWrite(subKey, "Registry write"), true))
                {
                    if (key == null)
                    {
                        error = "HKCU\\" + subKey + " is missing: start the game once.";
                        return false;
                    }
                    foreach (KeyValuePair<string, string> change in changes)
                        key.SetValue(change.Key, Encode(change.Value), RegistryValueKind.Binary);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not write the " + game + " bindings (" + location + "): " + ex.Message;
                return false;
            }
        }
    }
}
