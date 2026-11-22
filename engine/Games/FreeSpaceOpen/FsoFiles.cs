using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace JoyPro.Engine.Games.FreeSpaceOpen
{
    // A mod's files as FSO sees them (cfile): each package folder of the mod (mod.json "packages", Knossos passes them as
    // mod roots in that order) and the mod folder itself, loose files first, then the .vp archives of the root (VPVP
    // header, a directory of { offset, size, char name[32], time } where a size 0 entry enters a folder and ".." leaves it).
    // Only read, never written.
    internal sealed class FsoModFiles
    {
        private readonly List<string> roots = new List<string>();
        private List<KeyValuePair<string, Func<byte[]>>> vpEntries;

        public string ModFolder { get; private set; }
        public IReadOnlyList<string> Roots { get { return roots; } }

        public FsoModFiles(string modFolder)
        {
            ModFolder = modFolder ?? string.Empty;
            foreach (string package in FsoInstall.PackageFolders(ModFolder))
            {
                if (Directory.Exists(package) && !roots.Contains(package, StringComparer.OrdinalIgnoreCase))
                    roots.Add(package);
            }
            if (Directory.Exists(ModFolder) && !roots.Contains(ModFolder, StringComparer.OrdinalIgnoreCase))
                roots.Add(ModFolder);
        }

        // data/tables/<name> of the first root that has it (case does not matter, as on Windows), or null.
        public string ReadTable(string fileName, out string source)
        {
            source = string.Empty;
            foreach (string root in roots)
            {
                string folder = Path.Combine(root, "data", "tables");
                string path = Directory.Exists(folder) ? Directory.GetFiles(folder).FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase)) : null;
                if (path != null)
                {
                    source = path;
                    return File.ReadAllText(path, Encoding.UTF8);
                }
            }
            foreach (KeyValuePair<string, Func<byte[]>> entry in VpEntries())
            {
                if (string.Equals(entry.Key, "data/tables/" + fileName, StringComparison.OrdinalIgnoreCase))
                {
                    source = entry.Key;
                    return Encoding.UTF8.GetString(entry.Value());
                }
            }
            return null;
        }

        // Every data/tables file whose name ends with the suffix ("-lcl.tbm"), loose and in archives, as name -> text.
        public List<KeyValuePair<string, string>> ReadTables(string suffix)
        {
            List<KeyValuePair<string, string>> found = new List<KeyValuePair<string, string>>();
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                string folder = Path.Combine(root, "data", "tables");
                if (!Directory.Exists(folder))
                    continue;
                foreach (string path in Directory.GetFiles(folder).Where(f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (names.Add(Path.GetFileName(path)))
                        found.Add(new KeyValuePair<string, string>(path, File.ReadAllText(path, Encoding.UTF8)));
                }
            }
            foreach (KeyValuePair<string, Func<byte[]>> entry in VpEntries())
            {
                if (entry.Key.StartsWith("data/tables/", StringComparison.OrdinalIgnoreCase) && entry.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    names.Add(Path.GetFileName(entry.Key)))
                    found.Add(new KeyValuePair<string, string>(entry.Key, Encoding.UTF8.GetString(entry.Value())));
            }
            return found;
        }

        private List<KeyValuePair<string, Func<byte[]>>> VpEntries()
        {
            if (vpEntries != null)
                return vpEntries;
            vpEntries = new List<KeyValuePair<string, Func<byte[]>>>();
            foreach (string root in roots)
            {
                foreach (string vp in Directory.GetFiles(root, "*.vp").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    vpEntries.AddRange(FsoVp.TableEntries(vp));
            }
            return vpEntries;
        }
    }

    internal static class FsoVp
    {
        // The data/tables entries of a .vp archive ("data/tables/x.tbl" -> reader); empty for a file that is no VP.
        public static List<KeyValuePair<string, Func<byte[]>>> TableEntries(string path)
        {
            List<KeyValuePair<string, Func<byte[]>>> entries = new List<KeyValuePair<string, Func<byte[]>>>();
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    if (stream.Length < 16 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "VPVP")
                        return entries;
                    reader.ReadInt32();
                    int indexOffset = reader.ReadInt32();
                    int count = reader.ReadInt32();
                    if (indexOffset <= 0 || indexOffset >= stream.Length || count < 0 || (long)count * 44 > stream.Length)
                        return entries;
                    stream.Position = indexOffset;
                    List<string> folders = new List<string>();
                    for (int i = 0; i < count; i++)
                    {
                        int offset = reader.ReadInt32();
                        int size = reader.ReadInt32();
                        string name = Encoding.ASCII.GetString(reader.ReadBytes(32)).Split('\0')[0];
                        reader.ReadInt32();
                        if (size == 0)
                        {
                            if (name == "..")
                            {
                                if (folders.Count > 0)
                                    folders.RemoveAt(folders.Count - 1);
                            }
                            else
                            {
                                folders.Add(name);
                            }
                            continue;
                        }
                        string full = string.Join("/", folders.Concat(new[] { name }));
                        if (!full.StartsWith("data/tables/", StringComparison.OrdinalIgnoreCase))
                            continue;
                        string archive = path;
                        int o = offset;
                        int s = size;
                        entries.Add(new KeyValuePair<string, Func<byte[]>>(full, () => ReadRange(archive, o, s)));
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return entries;
        }

        private static byte[] ReadRange(string path, int offset, int size)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Position = offset;
                byte[] data = new byte[size];
                int read = 0;
                while (read < size)
                {
                    int n = stream.Read(data, read, size - read);
                    if (n <= 0)
                        break;
                    read += n;
                }
                return data;
            }
        }
    }

    // A mod's controls: FSO's hardcoded list with the mod's controlconfigdefaults.tbl applied (control_config_common_read_tbl:
    // a #ControlConfigOverride named "default" - or the first unnamed one - replaces the default preset and sets the
    // menu texts, tabs, types and the disabled flag; other sections are presets of their own), and the English texts the
    // controls menu shows (the XSTR ids of the mod's strings.tbl and *-lcl.tbm, else the table's text).
    internal sealed class FsoModControls
    {
        public List<FsoControl> Controls { get; private set; }
        // Preset name -> bindings by control id (primary, secondary); "default" is always there.
        public Dictionary<string, FsoBind[][]> Presets { get; private set; }
        public Dictionary<int, string> Strings { get; private set; }
        public string TableSource { get; set; }
        public List<string> Notes { get; private set; }

        public FsoModControls()
        {
            Controls = FsoControls.CopyDefaults();
            Presets = new Dictionary<string, FsoBind[][]>(StringComparer.Ordinal);
            Presets["default"] = Controls.Select(c => new[] { c.Primary, c.Secondary }).ToArray();
            Strings = new Dictionary<int, string>();
            TableSource = string.Empty;
            Notes = new List<string>();
        }

        public FsoControl Find(string name)
        {
            return Controls.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        }

        // The menu text in English.
        public string TitleOf(FsoControl control)
        {
            string text;
            if (control.XstrId > 0 && Strings.TryGetValue(control.XstrId, out text) && text.Trim().Length > 0)
                return text.Trim();
            return control.Text ?? control.Name;
        }

        public FsoBind[][] DefaultPreset
        {
            get { return Presets["default"]; }
        }

        public static FsoModControls Load(string modFolder, List<string> warnings)
        {
            FsoModControls mod = new FsoModControls();
            if (string.IsNullOrWhiteSpace(modFolder) || !Directory.Exists(modFolder))
                return mod;
            FsoModFiles files = new FsoModFiles(modFolder);
            string source;
            string table = files.ReadTable("controlconfigdefaults.tbl", out source);
            if (table != null)
            {
                mod.TableSource = source;
                FsoTables.Apply(mod, table, warnings);
            }
            string stringsSource;
            string strings = files.ReadTable("strings.tbl", out stringsSource);
            if (strings != null)
                FsoTables.ReadStrings(strings, mod.Strings);
            foreach (KeyValuePair<string, string> lcl in files.ReadTables("-lcl.tbm"))
                FsoTables.ReadStrings(lcl.Value, mod.Strings);
            return mod;
        }
    }

    internal static class FsoTables
    {
        // FSO comments: ';' to the end of the line and /* ... */.
        private static string StripComments(string text)
        {
            string noBlocks = Regex.Replace(text ?? string.Empty, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            StringBuilder sb = new StringBuilder();
            foreach (string raw in noBlocks.Replace("\r\n", "\n").Split('\n'))
            {
                int semi = raw.IndexOf(';');
                sb.Append(semi >= 0 ? raw.Substring(0, semi) : raw).Append('\n');
            }
            return sb.ToString();
        }

        private sealed class Token
        {
            public string Key;
            public string Value;
        }

        // "$Key: value" / "+Flag" / "#Section" lines as tokens (FSO's parser takes a value to the end of its line).
        private static List<Token> Tokens(string text)
        {
            List<Token> tokens = new List<Token>();
            foreach (string raw in StripComments(text).Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;
                if (line[0] == '#')
                {
                    tokens.Add(new Token { Key = line.Split(new[] { ' ', '\t' }, 2)[0], Value = string.Empty });
                    continue;
                }
                if (line[0] == '+' && line.IndexOf(':') < 0)
                {
                    tokens.Add(new Token { Key = line.Split(new[] { ' ', '\t' }, 2)[0], Value = string.Empty });
                    continue;
                }
                int colon = line.IndexOf(':');
                if ((line[0] == '$' || line[0] == '+') && colon > 0)
                    tokens.Add(new Token { Key = line.Substring(0, colon + 1), Value = line.Substring(colon + 1).Trim() });
            }
            return tokens;
        }

        private static bool Boolean(string value)
        {
            string v = (value ?? string.Empty).Trim().ToLowerInvariant();
            return v == "yes" || v == "true" || v == "1" || v == "on";
        }

        public static void Apply(FsoModControls mod, string text, List<string> warnings)
        {
            List<Token> tokens = Tokens(text);
            int sections = 0;
            int i = 0;
            while (i < tokens.Count)
            {
                Token token = tokens[i];
                bool overrideSection = token.Key.Equals("#ControlConfigOverride", StringComparison.OrdinalIgnoreCase);
                bool presetSection = token.Key.Equals("#ControlConfigPreset", StringComparison.OrdinalIgnoreCase);
                if (!overrideSection && !presetSection)
                {
                    i++;
                    continue;
                }
                sections++;
                i++;
                string name = null;
                if (i < tokens.Count && tokens[i].Key == "$Name:")
                {
                    name = tokens[i].Value;
                    i++;
                }
                if (name == null)
                    name = overrideSection && sections == 1 ? "default" : "<unnamed preset>";
                FsoBind[][] bindings = overrideSection
                    ? mod.DefaultPreset.Select(b => new[] { b[0], b[1] }).ToArray()
                    : Enumerable.Range(0, FsoNames.ControlCount).Select(_ => new[] { FsoBind.None, FsoBind.None }).ToArray();
                FsoControl item = null;
                // An override item without $Disable / +Disable is enabled (read_section sets disabled = false).
                bool? itemDisable = null;
                while (i < tokens.Count && !tokens[i].Key.Equals("#End", StringComparison.OrdinalIgnoreCase) && !tokens[i].Key.StartsWith("#", StringComparison.Ordinal))
                {
                    Token t = tokens[i];
                    switch (t.Key)
                    {
                        case "$Bind:":
                            item = mod.Find(t.Value);
                            if (item == null)
                                warnings?.Add("controlconfigdefaults.tbl: unknown bind '" + t.Value + "' (skipped).");
                            itemDisable = null;
                            i++;
                            break;
                        case "$Bind Name:":
                            item = mod.Controls.FirstOrDefault(c => string.Equals(c.Text, t.Value, StringComparison.OrdinalIgnoreCase));
                            itemDisable = null;
                            i = ReadLegacyBind(tokens, i + 1, item, bindings);
                            break;
                        case "$Primary:":
                        case "$Secondary:":
                            {
                                int slot = t.Key == "$Primary:" ? 0 : 1;
                                FsoBind bind;
                                i = ReadBind(tokens, i + 1, out bind);
                                if (item != null)
                                    bindings[item.Id][slot] = bind;
                                break;
                            }
                        case "$Category:":
                            if (item != null && overrideSection)
                            {
                                char tab = FsoNames.TabFromName(t.Value);
                                if (tab != 0)
                                    item.Tab = tab;
                            }
                            i++;
                            break;
                        case "$Text:":
                            if (item != null && overrideSection)
                            {
                                item.Text = t.Value;
                                item.Xstr = 0;
                            }
                            i++;
                            break;
                        case "$Has XStr:":
                            {
                                int id;
                                if (item != null && overrideSection && int.TryParse(t.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                                    item.Xstr = id;
                                i++;
                                break;
                            }
                        case "$Type:":
                            if (item != null && overrideSection)
                            {
                                string type = FsoNames.TypeFromName(t.Value);
                                if (type.Length > 0)
                                    item.Type = type;
                            }
                            i++;
                            break;
                        case "$Disable:":
                            itemDisable = Boolean(t.Value);
                            if (item != null && overrideSection)
                                item.Disabled = itemDisable.Value;
                            i++;
                            break;
                        case "+Disable":
                            itemDisable = true;
                            if (item != null && overrideSection)
                                item.Disabled = true;
                            i++;
                            break;
                        default:
                            i++;
                            break;
                    }
                    if (item != null && overrideSection && itemDisable == null)
                        item.Disabled = false;
                }
                if (i < tokens.Count && tokens[i].Key.Equals("#End", StringComparison.OrdinalIgnoreCase))
                    i++;
                if (overrideSection && name == "default")
                    mod.Presets["default"] = bindings;
                else if (!mod.Presets.ContainsKey(name))
                    mod.Presets[name] = bindings;
            }
            // A control without a tab stays hidden (CCI_builder).
            foreach (FsoControl control in mod.Controls.Where(c => c.Tab == 'N'))
                control.Disabled = true;
        }

        // $Controller: [$Flags:] [$Input:] after $Primary: / $Secondary: (read_bind_1).
        private static int ReadBind(List<Token> tokens, int i, out FsoBind bind)
        {
            bind = FsoBind.None;
            string cid = "CID_NONE";
            string flags = "NONE";
            string input = "NONE";
            if (i < tokens.Count && tokens[i].Key == "$Controller:")
            {
                cid = tokens[i].Value.Trim();
                i++;
            }
            if (cid != "CID_NONE")
            {
                if (i < tokens.Count && tokens[i].Key == "$Flags:")
                {
                    flags = tokens[i].Value;
                    i++;
                }
                if (i < tokens.Count && tokens[i].Key == "$Input:")
                {
                    input = tokens[i].Value.Trim();
                    i++;
                }
            }
            bind = FsoBind.Make(cid, flags, input);
            return i;
        }

        // The old bind format (read_bind_0): $Key Default:, $Key Mod Shift / Alt / Ctrl:, $Joy Default: (Joy-0 button).
        private static int ReadLegacyBind(List<Token> tokens, int i, FsoControl item, FsoBind[][] bindings)
        {
            string key = null;
            bool shift = false;
            bool alt = false;
            int? joy = null;
            while (i < tokens.Count && (tokens[i].Key.StartsWith("$Key ", StringComparison.Ordinal) || tokens[i].Key == "$Joy Default:"))
            {
                Token t = tokens[i];
                int n;
                if (t.Key == "$Key Default:")
                    key = t.Value == "NONE" ? string.Empty : t.Value;
                else if (t.Key == "$Key Mod Shift:")
                    shift = int.TryParse(t.Value, out n) && n == 1;
                else if (t.Key == "$Key Mod Alt:")
                    alt = int.TryParse(t.Value, out n) && n == 1;
                else if (t.Key == "$Joy Default:" && int.TryParse(t.Value, out n))
                    joy = n;
                i++;
            }
            if (item == null)
                return i;
            if (key != null)
                bindings[item.Id][0] = key.Length == 0 ? FsoBind.None : FsoBind.Make("CID_KEYBOARD", "NONE", (alt ? "ALT-" : string.Empty) + (shift ? "SHIFT-" : string.Empty) + key);
            if (joy.HasValue)
                bindings[item.Id][1] = FsoBind.Make("CID_JOY0", "NONE", joy.Value.ToString(CultureInfo.InvariantCulture));
            return i;
        }

        private static readonly Regex StringLine = new Regex("^\\s*(\\d+)\\s*,\\s*\"([^\"]*)\"", RegexOptions.Compiled);

        // The #English part of strings.tbl / *-lcl.tbm: 'id, "text"' lines (later files win).
        public static void ReadStrings(string text, Dictionary<int, string> strings)
        {
            bool english = false;
            foreach (string raw in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    english = line.StartsWith("#English", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!english)
                    continue;
                Match match = StringLine.Match(raw);
                int id;
                if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                    strings[id] = match.Groups[2].Value;
            }
        }
    }

    // A controls preset file, data/players/presets/<name>.json (controlconfig/presets.cpp): "signature" 0x5f545350 ("PST_"),
    // "version" 0 and "actions": one { "bind", "primary" { "cid", "flags", "input" }, "secondary" { ... } } per control.
    // FSO reads every listed control (unknown names are skipped), a control the file does not list is unbound, and the
    // file name is the preset's name. Written as FSO's save_preset_file writes it (all controls in id order, jansson with
    // an indent of 4).
    internal static class FsoPresetFile
    {
        public const int Signature = 0x5f545350;
        public const int Version = 0;

        public static FsoBind[][] Read(string path, List<string> errors)
        {
            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                errors.Add("Could not read the preset " + path + ": " + ex.Message);
                return null;
            }
            return Parse(text, path, errors);
        }

        public static FsoBind[][] Parse(string text, string path, List<string> errors)
        {
            Dictionary<string, object> root;
            try
            {
                root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text) as Dictionary<string, object>;
            }
            catch (Exception ex)
            {
                errors.Add("The preset " + path + " is no valid JSON: " + ex.Message);
                return null;
            }
            object signature;
            if (root == null || !root.TryGetValue("signature", out signature) || Convert.ToInt64(signature, CultureInfo.InvariantCulture) != Signature)
            {
                errors.Add("Not an FSO controls preset (signature PST_): " + path);
                return null;
            }
            FsoBind[][] bindings = Enumerable.Range(0, FsoNames.ControlCount).Select(_ => new[] { FsoBind.None, FsoBind.None }).ToArray();
            object actions;
            object[] list = root.TryGetValue("actions", out actions) ? actions as object[] : null;
            if (list == null)
                return bindings;
            foreach (Dictionary<string, object> entry in list.OfType<Dictionary<string, object>>())
            {
                FsoControl control = FsoControls.Find(Str(entry, "bind"));
                if (control == null)
                    continue;
                bindings[control.Id][0] = BindOf(entry, "primary");
                bindings[control.Id][1] = BindOf(entry, "secondary");
            }
            return bindings;
        }

        private static string Str(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty;
        }

        private static FsoBind BindOf(Dictionary<string, object> entry, string key)
        {
            object value;
            Dictionary<string, object> obj = entry.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
            return obj == null ? FsoBind.None : FsoBind.Make(Str(obj, "cid"), Str(obj, "flags"), Str(obj, "input"));
        }

        public static string Render(FsoBind[][] bindings)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n    \"signature\": ").Append(Signature.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("    \"version\": ").Append(Version.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("    \"actions\": [\n");
            for (int id = 0; id < FsoNames.ControlCount; id++)
            {
                FsoBind first = bindings[id][0].Validated();
                FsoBind second = bindings[id][1].Validated();
                sb.Append("        {\n");
                sb.Append("            \"bind\": \"").Append(FsoControls.Defaults[id].Name).Append("\",\n");
                AppendBind(sb, "primary", first, true);
                AppendBind(sb, "secondary", second, false);
                sb.Append("        }").Append(id + 1 < FsoNames.ControlCount ? ",\n" : "\n");
            }
            sb.Append("    ]\n}");
            return sb.ToString();
        }

        private static void AppendBind(StringBuilder sb, string key, FsoBind bind, bool comma)
        {
            sb.Append("            \"").Append(key).Append("\": {\n");
            sb.Append("                \"cid\": \"").Append(Escape(bind.Cid)).Append("\",\n");
            sb.Append("                \"flags\": \"").Append(Escape(bind.Flags)).Append("\",\n");
            sb.Append("                \"input\": \"").Append(Escape(bind.Input)).Append("\"\n");
            sb.Append("            }").Append(comma ? ",\n" : "\n");
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // FSO ignores a preset that binds every enabled control as an existing one (preset_find_duplicate), and the
        // pilot then names the other preset.
        public static bool SameBindings(FsoBind[][] a, FsoBind[][] b, IList<FsoControl> controls)
        {
            for (int id = 0; id < FsoNames.ControlCount; id++)
            {
                if (controls != null && id < controls.Count && controls[id].Disabled)
                    continue;
                if (!a[id][0].Same(b[id][0]) || !a[id][1].Same(b[id][1]))
                    return false;
            }
            return true;
        }
    }

    // A pilot file, data/players/<callsign>.json (pilotfile/plr.cpp, PLR version 4): "signature" 0x5f524c50 ("PLR_"),
    // sections.controls.preset names the controls preset (the bindings live in the preset since PLR 4; the "controls" /
    // "axes" arrays only keep the defaults for old builds), sections.settings joy_sensitivity / joy_dead_zone_size.
    // JoyPro changes those values in the text, so every other byte stays as the game wrote it.
    internal sealed class FsoPilot
    {
        public const long Signature = 0x5f524c50;
        private static readonly Regex PresetValue = new Regex("(\"preset\"\\s*:\\s*\")((?:[^\"\\\\]|\\\\.)*)(\")", RegexOptions.Compiled);

        public string Path;
        public string Callsign;
        public int Version;
        public string Preset;
        public int? Sensitivity;
        public int? DeadZone;
        public string Text;

        public static FsoPilot Read(string path, List<string> errors)
        {
            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                errors?.Add("Could not read the pilot " + path + ": " + ex.Message);
                return null;
            }
            Dictionary<string, object> root;
            try
            {
                root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                errors?.Add("The pilot " + path + " is no valid JSON.");
                return null;
            }
            object signature;
            if (root == null || !root.TryGetValue("signature", out signature) || Convert.ToInt64(signature, CultureInfo.InvariantCulture) != Signature)
            {
                errors?.Add("Not an FSO pilot file (signature PLR_): " + path);
                return null;
            }
            FsoPilot pilot = new FsoPilot { Path = path, Callsign = System.IO.Path.GetFileNameWithoutExtension(path), Text = text };
            object version;
            pilot.Version = root.TryGetValue("version", out version) ? Convert.ToInt32(version, CultureInfo.InvariantCulture) : 0;
            object sectionsValue;
            Dictionary<string, object> sections = root.TryGetValue("sections", out sectionsValue) ? sectionsValue as Dictionary<string, object> : null;
            object controlsValue;
            Dictionary<string, object> controls = sections != null && sections.TryGetValue("controls", out controlsValue) ? controlsValue as Dictionary<string, object> : null;
            object preset;
            pilot.Preset = controls != null && controls.TryGetValue("preset", out preset) && preset != null ? Convert.ToString(preset, CultureInfo.InvariantCulture) : string.Empty;
            object settingsValue;
            Dictionary<string, object> settings = sections != null && sections.TryGetValue("settings", out settingsValue) ? settingsValue as Dictionary<string, object> : null;
            pilot.Sensitivity = IntOf(settings, "joy_sensitivity");
            pilot.DeadZone = IntOf(settings, "joy_dead_zone_size");
            return pilot;
        }

        private static int? IntOf(Dictionary<string, object> obj, string key)
        {
            object value;
            if (obj == null || !obj.TryGetValue(key, out value) || value == null)
                return null;
            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // The pilot text with the preset name set; null when the file has not exactly one "preset" value.
        public string WithPreset(string text, string name)
        {
            MatchCollection matches = PresetValue.Matches(text ?? string.Empty);
            if (matches.Count != 1)
                return null;
            Match m = matches[0];
            return text.Substring(0, m.Groups[2].Index) + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + text.Substring(m.Groups[2].Index + m.Groups[2].Length);
        }

        // The pilot text with an integer setting set; null when the key is not there exactly once.
        public static string WithSetting(string text, string key, int value)
        {
            Regex regex = new Regex("(\"" + Regex.Escape(key) + "\"\\s*:\\s*)(-?\\d+)");
            MatchCollection matches = regex.Matches(text ?? string.Empty);
            if (matches.Count != 1)
                return null;
            Group g = matches[0].Groups[2];
            return text.Substring(0, g.Index) + value.ToString(CultureInfo.InvariantCulture) + text.Substring(g.Index + g.Length);
        }

        // data/players/<callsign>.*.csg: the campaign saves of the pilot.
        public List<string> CampaignSaves()
        {
            string folder = System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
            if (!Directory.Exists(folder))
                return new List<string>();
            return Directory.GetFiles(folder, Callsign + ".*.csg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    // A campaign save (pilotfile/csg.cpp, CSG version >= 7): "CSG_", a version byte, then sections { ushort id, uint
    // size, data }. Section 0x0004 (Controls) holds the preset name as a NUL-terminated string; when the campaign is
    // loaded FSO switches the pilot to that preset, so the export sets it there too.
    internal static class FsoCampaignSave
    {
        private const ushort ControlsSection = 0x0004;

        public static string ReadPreset(byte[] data)
        {
            int start;
            int size;
            return Find(data, out start, out size) ? Encoding.UTF8.GetString(data, start, Math.Max(0, size - 1)).TrimEnd('\0') : null;
        }

        private static bool Find(byte[] data, out int start, out int size)
        {
            start = 0;
            size = 0;
            if (data == null || data.Length < 5 || Encoding.ASCII.GetString(data, 0, 4) != "CSG_" || data[4] < 7)
                return false;
            int p = 5;
            while (p + 6 <= data.Length)
            {
                ushort id = BitConverter.ToUInt16(data, p);
                int length = BitConverter.ToInt32(data, p + 2);
                if (length < 0 || p + 6 + length > data.Length)
                    return false;
                if (id == ControlsSection)
                {
                    start = p + 6;
                    size = length;
                    return true;
                }
                p += 6 + length;
            }
            return false;
        }

        // The save with the preset name set (null: no Controls section, an old version, or nothing to change).
        public static byte[] WithPreset(byte[] data, string name)
        {
            int start;
            int size;
            if (!Find(data, out start, out size))
                return null;
            byte[] value = Encoding.UTF8.GetBytes(name + "\0");
            if (size == value.Length && data.Skip(start).Take(size).SequenceEqual(value))
                return null;
            byte[] result = new byte[data.Length - size + value.Length];
            Buffer.BlockCopy(data, 0, result, 0, start);
            Buffer.BlockCopy(value, 0, result, start, value.Length);
            Buffer.BlockCopy(data, start + size, result, start + value.Length, data.Length - start - size);
            byte[] length = BitConverter.GetBytes(value.Length);
            Buffer.BlockCopy(length, 0, result, start - 4, 4);
            return result;
        }
    }

    // The mod's settings (osapi/osregistry.cpp): data/<mod folder without its version>_settings.ini when the mod uses the
    // in-game options (FSO 23+), else fs2_open.ini [Default]. [Input] Joystick, Joystick1..3 = {"guid":"<SDL GUID>","id":
    // <device index>} pick the devices FSO uses as Joy-0..3; JoystickSensitivity / JoystickDeadZone the axis response.
    internal sealed class FsoIni
    {
        private readonly List<string> lines;
        private readonly string newline;

        public string Path { get; private set; }
        public bool Changed { get; private set; }

        private FsoIni(string path, string text)
        {
            Path = path;
            newline = (text ?? string.Empty).Contains("\r\n") ? "\r\n" : "\n";
            lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n').ToList();
        }

        public static FsoIni Load(string path)
        {
            return new FsoIni(path, File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty);
        }

        public string Get(string section, string key)
        {
            string current = string.Empty;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    current = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq > 0 && string.Equals(current, section, StringComparison.OrdinalIgnoreCase) && string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(eq + 1).Trim();
            }
            return null;
        }

        // Sets the value in place, or adds the key at the end of its section (the section at the end of the file).
        public void Set(string section, string key, string value)
        {
            string current = string.Empty;
            int sectionEnd = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    current = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                if (!string.Equals(current, section, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (line.Length > 0)
                    sectionEnd = i;
                int eq = line.IndexOf('=');
                if (eq > 0 && string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    if (line.Substring(eq + 1).Trim() == value)
                        return;
                    lines[i] = key + "=" + value;
                    Changed = true;
                    return;
                }
            }
            if (sectionEnd < 0)
            {
                int header = lines.FindIndex(l => string.Equals(l.Trim(), "[" + section + "]", StringComparison.OrdinalIgnoreCase));
                if (header >= 0)
                {
                    lines.Insert(header + 1, key + "=" + value);
                }
                else
                {
                    while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0)
                        lines.RemoveAt(lines.Count - 1);
                    if (lines.Count > 0)
                        lines.Add(string.Empty);
                    lines.Add("[" + section + "]");
                    lines.Add(key + "=" + value);
                    lines.Add(string.Empty);
                }
            }
            else
            {
                lines.Insert(sectionEnd + 1, key + "=" + value);
            }
            Changed = true;
        }

        public string Render()
        {
            return string.Join(newline, lines);
        }
    }

    // A device slot: the SDL joystick GUID and the device index FSO saved for it.
    internal sealed class FsoSlot
    {
        public int Index;
        public string Guid = string.Empty;
        public int Id = -1;

        // SDL's joystick GUID (SDL_CreateJoystickGUID): bytes 0-1 bus, 2-3 CRC-16 of the name, 4-5 vendor, 8-9 product
        // (little endian), as 32 upper-case hex digits.
        public int Vendor
        {
            get { return Word(4); }
        }

        public int Product
        {
            get { return Word(8); }
        }

        public int Crc
        {
            get { return Word(2); }
        }

        private int Word(int offset)
        {
            if (Guid.Length != 32)
                return -1;
            int lo;
            int hi;
            if (!int.TryParse(Guid.Substring(offset * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out lo) ||
                !int.TryParse(Guid.Substring(offset * 2 + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hi))
                return -1;
            return lo | (hi << 8);
        }

        private static readonly Regex Json = new Regex("\"guid\"\\s*:\\s*\"([0-9A-Fa-f]*)\"\\s*,\\s*\"id\"\\s*:\\s*(-?\\d+)", RegexOptions.Compiled);

        public static FsoSlot Parse(int index, string value)
        {
            Match m = Json.Match(value ?? string.Empty);
            if (!m.Success)
                return null;
            return new FsoSlot { Index = index, Guid = m.Groups[1].Value.ToUpperInvariant(), Id = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) };
        }

        // SDL_crc16 (CRC-16/ARC: polynomial 0xA001 reflected, start 0) of the device name, as SDL puts it in the GUID.
        public static int Crc16(string name)
        {
            int crc = 0;
            foreach (byte b in Encoding.UTF8.GetBytes(name ?? string.Empty))
            {
                int r = (crc ^ b) & 0xFF;
                int c = 0;
                for (int i = 0; i < 8; i++)
                {
                    c = (((c ^ r) & 1) != 0 ? 0xA001 : 0) ^ (c >> 1);
                    r >>= 1;
                }
                crc = c ^ (crc >> 8);
            }
            return crc & 0xFFFF;
        }
    }
}
