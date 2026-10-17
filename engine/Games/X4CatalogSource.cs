using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine.Games
{
    // The X4 control catalog as the installed game defines it (see docs/games/x4.md "Action catalog"): the rows of the
    // Options > Controls menu are config.input.controlsorder in ui/addons/ego_gameoptions/gameoptions.lua (sections
    // space, menus, firstperson), the bind-together groups config.input.controlFunctions; titles come from
    // t/0001-l044.xml (pages 1005 actions, 1006 states, 1007 ranges, 1001 group names). The Lua menu uses numeric
    // ids; their names come from the name tables in X4.exe, else from DB\X4\order.tsv. Everything is read-only.
    internal static class X4CatalogSource
    {
        public const string MenuScript = "ui/addons/ego_gameoptions/gameoptions.lua";
        public const string EnglishTexts = "t/0001-l044.xml";

        private static readonly Dictionary<string, string> Element = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "actions", "action" }, { "states", "state" }, { "ranges", "range" }
        };
        private static readonly Dictionary<string, int> TitlePage = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "actions", 1005 }, { "states", 1006 }, { "ranges", 1007 }
        };
        // Space groups that are no ship flight (save/load/quit, reduced game speed, debug and HUD toggles, mouse-only).
        private static readonly HashSet<string> NotFlightGroups = new HashSet<string>(StringComparer.Ordinal)
        {
            "Options Menu", "Accessibility", "Expert Settings - Use with Caution!", "Target Management (Mouse)"
        };
        // Space rows that only work in the map, leave the pilot seat or are game/system controls.
        private static readonly HashSet<string> NotFlightIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "INPUT_ACTION_ADDON_DETAILMONITOR_T", "INPUT_ACTION_ADDON_DETAILMONITOR_CONTEXT_MENU", "INPUT_ACTION_GET_UP",
            "INPUT_ACTION_PAUSE", "INPUT_ACTION_SCREENSHOT", "INPUT_ACTION_MUTE"
        };
        private const string SharedWithOnFoot = "INPUT_STATE_INTERACTION_MENU";

        internal sealed class Row
        {
            public string Id = string.Empty;
            public string Title = string.Empty;
            public bool IsAxis;
            public string Category = string.Empty;
            public bool Flight;
        }

        internal sealed class Result
        {
            public string Source = string.Empty;
            public List<Row> Rows = new List<Row>();
            // defining id -> member ids (actions, states, ranges in menu order).
            public List<KeyValuePair<string, List<string>>> Functions = new List<KeyValuePair<string, List<string>>>();
            // Every control of the exe's name tables: element, numeric, id (empty without X4.exe).
            public List<Tuple<string, int, string>> ExeControls = new List<Tuple<string, int, string>>();
        }

        // source: the X4 folder (01.cat ... with X4.exe) or a folder with the two files extracted (same relative paths or
        // anywhere below). knownIds: element -> numeric -> id from order.tsv.
        public static Result Read(string source, Dictionary<string, Dictionary<int, string>> knownIds, List<string> warnings, List<string> errors)
        {
            Dictionary<string, byte[]> files = ReadSourceFiles(source, warnings, errors);
            if (files == null)
                return null;
            byte[] lua;
            byte[] texts;
            if (!files.TryGetValue(MenuScript, out lua) || !files.TryGetValue(EnglishTexts, out texts))
            {
                errors.Add("X4's " + (lua == null ? MenuScript : EnglishTexts) + " was not found in " + source + ".");
                return null;
            }

            Result result = new Result { Source = source };
            Dictionary<string, Dictionary<int, string>> ids = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
            foreach (string element in Element.Values)
                ids[element] = new Dictionary<int, string>(knownIds != null && knownIds.ContainsKey(element) ? knownIds[element] : new Dictionary<int, string>());
            string exe = Path.Combine(source, "X4.exe");
            if (File.Exists(exe))
            {
                try
                {
                    result.ExeControls = X4ExeNames.Read(exe);
                }
                catch (Exception ex)
                {
                    warnings.Add("X4.exe's control name tables could not be read (" + ex.Message + "); ids come from order.tsv only.");
                }
                foreach (Tuple<string, int, string> control in result.ExeControls)
                {
                    string known;
                    if (ids[control.Item1].TryGetValue(control.Item2, out known) && known != control.Item3)
                        warnings.Add("X4.exe names " + control.Item1 + " " + control.Item2.ToString(CultureInfo.InvariantCulture) + " '" + control.Item3 + "', order.tsv '" + known + "'; the game's name is used.");
                    ids[control.Item1][control.Item2] = control.Item3;
                }
                if (result.ExeControls.Count == 0)
                    warnings.Add("X4.exe's control name tables were not found; ids come from order.tsv only.");
            }

            Dictionary<int, Dictionary<int, string>> pages = ReadPages(Encoding.UTF8.GetString(texts));
            string script = Encoding.UTF8.GetString(lua);
            Dictionary<int, LuaFunction> functions = ParseFunctions(script, pages);
            string order = TableAfter(script, "config.input.controlsorder =");
            if (functions == null || order == null)
            {
                errors.Add(MenuScript + " has no config.input.controlsorder / controlFunctions table.");
                return null;
            }

            Func<string, int, string> idOf = (type, number) =>
            {
                string id;
                return ids[Element[type]].TryGetValue(number, out id) ? id : null;
            };
            Dictionary<string, Row> byId = new Dictionary<string, Row>(StringComparer.Ordinal);
            Dictionary<string, List<string>> groupsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            Dictionary<string, List<string>> notesOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> section in LuaTable.Entries(order))
            {
                string sectionName = Unquote(section.Key);
                foreach (KeyValuePair<string, string> group in LuaTable.Entries(section.Value))
                {
                    List<KeyValuePair<string, string>> fields = LuaTable.Entries(group.Value);
                    string groupTitle = ReadText(fields.Where(f => f.Key == "[\"title\"]").Select(f => f.Value).FirstOrDefault(), pages);
                    bool mappable = fields.Any(f => f.Key == "[\"mappable\"]" && f.Value.Trim() == "true");
                    bool mouseOnly = fields.Any(f => f.Key == "[\"mouseonly\"]" && f.Value.Trim() == "true");
                    string category = sectionName + "/" + groupTitle;
                    foreach (string control in fields.Where(f => f.Key == null).Select(f => f.Value))
                    {
                        List<KeyValuePair<string, string>> parts = LuaTable.Entries(control);
                        if (parts.Count < 2 || parts[0].Key != null || parts[1].Key != null)
                            continue;
                        string type = Unquote(parts[0].Value);
                        int number;
                        if (!int.TryParse(parts[1].Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                            continue;
                        List<string> flags = parts.Where(p => (p.Key == "mousewheelonly" || p.Key == "mouseaxisonly") && p.Value.Trim() == "true").Select(p => p.Key).ToList();
                        if (mouseOnly)
                            flags.Add("mouseonly");

                        string title;
                        int? function = null;
                        if (type == "functions")
                        {
                            LuaFunction f;
                            if (!functions.TryGetValue(number, out f))
                                continue;
                            function = number;
                            type = f.DefiningType;
                            title = f.Name;
                            number = f.DefiningNumber;
                        }
                        else if (TitlePage.ContainsKey(type))
                        {
                            title = Resolve(pages, TitlePage[type], number, 0);
                        }
                        else
                        {
                            continue;
                        }
                        if (!TitlePage.ContainsKey(type))
                            continue;
                        string id = idOf(type, number);
                        if (id == null)
                        {
                            warnings.Add("X4 menu control " + type + " " + number.ToString(CultureInfo.InvariantCulture) + " has no known id (X4.exe not read or the control is new); skipped.");
                            continue;
                        }
                        if (!groupsOf.ContainsKey(id))
                            groupsOf[id] = new List<string>();
                        groupsOf[id].Add(category);
                        if (byId.ContainsKey(id))
                            continue;
                        List<string> notes = new List<string>();
                        if (!mappable)
                            notes.Add("fixed in menu");
                        notes.AddRange(flags);
                        if (function.HasValue)
                            notes.Add("function " + function.Value.ToString(CultureInfo.InvariantCulture));
                        if (id == SharedWithOnFoot)
                            notes.Add("shared with on-foot Interact");
                        notesOf[id] = notes;
                        Row row = new Row
                        {
                            Id = id,
                            Title = title.Length > 0 ? title : id,
                            IsAxis = type == "ranges",
                            Category = category,
                            Flight = sectionName == "space" && !NotFlightGroups.Contains(groupTitle) && !NotFlightIds.Contains(id) && flags.Count == 0
                        };
                        byId[id] = row;
                        result.Rows.Add(row);
                    }
                }
            }
            foreach (Row row in result.Rows)
            {
                List<string> notes = notesOf[row.Id];
                List<string> also = groupsOf[row.Id].Where(g => g != row.Category).Distinct().OrderBy(g => g, StringComparer.Ordinal).ToList();
                if (also.Count > 0)
                    notes.Add("also in " + string.Join(", ", also));
                if (notes.Count > 0)
                    row.Category += "; " + string.Join("; ", notes);
            }
            foreach (KeyValuePair<int, LuaFunction> function in functions.OrderBy(f => f.Key))
            {
                string defining = idOf(function.Value.DefiningType, function.Value.DefiningNumber);
                List<string> members = function.Value.Members.Select(m => idOf(m.Key, m.Value)).ToList();
                if (defining == null || members.Any(m => m == null))
                {
                    warnings.Add("X4 menu function " + function.Key.ToString(CultureInfo.InvariantCulture) + " has controls without a known id; skipped.");
                    continue;
                }
                result.Functions.Add(new KeyValuePair<string, List<string>>(defining, members));
            }
            return result;
        }

        // ---- files --------------------------------------------------------------------------------------------------

        private static Dictionary<string, byte[]> ReadSourceFiles(string source, List<string> warnings, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
            {
                errors.Add("X4 folder not found: " + source);
                return null;
            }
            List<string> wanted = new List<string> { MenuScript, EnglishTexts };
            if (X4Archives.Catalogs(source).Count > 0)
            {
                try
                {
                    return X4Archives.ReadFiles(source, wanted);
                }
                catch (Exception ex)
                {
                    errors.Add("Could not read X4's archives in " + source + ": " + ex.Message);
                    return null;
                }
            }
            // A folder of extracted files: the same relative path, else the file name anywhere below.
            Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (string path in wanted)
            {
                string direct = Path.Combine(source, path.Replace('/', Path.DirectorySeparatorChar));
                string found = File.Exists(direct) ? direct : Directory.EnumerateFiles(source, Path.GetFileName(path), SearchOption.AllDirectories).FirstOrDefault();
                if (found != null)
                    files[path] = File.ReadAllBytes(found);
            }
            return files;
        }

        // ---- text pages ---------------------------------------------------------------------------------------------

        private static readonly Regex PageRegex = new Regex(@"<page id=""(\d+)""[^>]*>(.*?)</page>", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex TextRegex = new Regex(@"<t id=""(\d+)"">(.*?)</t>", RegexOptions.Singleline | RegexOptions.Compiled);
        // An unescaped "(...)" is a translator comment the game does not show.
        private static readonly Regex Comment = new Regex(@"(?<!\\)\((?:\\.|[^()\\])*\)", RegexOptions.Compiled);
        private static readonly Regex Reference = new Regex(@"\{(\d+),\s*(\d+)\}", RegexOptions.Compiled);

        private static Dictionary<int, Dictionary<int, string>> ReadPages(string xml)
        {
            Dictionary<int, Dictionary<int, string>> pages = new Dictionary<int, Dictionary<int, string>>();
            foreach (Match page in PageRegex.Matches(xml))
            {
                Dictionary<int, string> texts = new Dictionary<int, string>();
                foreach (Match text in TextRegex.Matches(page.Groups[2].Value))
                    texts[int.Parse(text.Groups[1].Value, CultureInfo.InvariantCulture)] = text.Groups[2].Value;
                pages[int.Parse(page.Groups[1].Value, CultureInfo.InvariantCulture)] = texts;
            }
            return pages;
        }

        private static string Resolve(Dictionary<int, Dictionary<int, string>> pages, int page, int id, int depth)
        {
            Dictionary<int, string> texts;
            string text;
            if (!pages.TryGetValue(page, out texts) || !texts.TryGetValue(id, out text))
                return string.Empty;
            text = Comment.Replace(text, string.Empty);
            if (depth < 6)
                text = Reference.Replace(text, m => Resolve(pages, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), depth + 1));
            text = WebUtility.HtmlDecode(text.Replace("\\(", "(").Replace("\\)", ")"));
            return string.Join(" ", text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string ReadText(string luaValue, Dictionary<int, Dictionary<int, string>> pages)
        {
            Match match = Regex.Match(luaValue ?? string.Empty, @"ReadText\((\d+),\s*(\d+)\)");
            return match.Success ? Resolve(pages, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), 0) : string.Empty;
        }

        // ---- gameoptions.lua ----------------------------------------------------------------------------------------

        private sealed class LuaFunction
        {
            public string Name = string.Empty;
            public string DefiningType = string.Empty;
            public int DefiningNumber;
            public List<KeyValuePair<string, int>> Members = new List<KeyValuePair<string, int>>();
        }

        private static Dictionary<int, LuaFunction> ParseFunctions(string script, Dictionary<int, Dictionary<int, string>> pages)
        {
            string table = TableAfter(script, "config.input.controlFunctions =");
            if (table == null)
                return null;
            Dictionary<int, LuaFunction> functions = new Dictionary<int, LuaFunction>();
            foreach (KeyValuePair<string, string> entry in LuaTable.Entries(table))
            {
                Match key = Regex.Match(entry.Key ?? string.Empty, @"^\[(\d+)\]$");
                if (!key.Success)
                    continue;
                LuaFunction function = new LuaFunction();
                foreach (KeyValuePair<string, string> field in LuaTable.Entries(entry.Value))
                {
                    if (field.Key == "[\"name\"]")
                        function.Name = ReadText(field.Value, pages);
                    else if (field.Key == "[\"definingcontrol\"]")
                    {
                        Match defining = Regex.Match(field.Value, @"\{\s*""(\w+)""\s*,\s*(\d+)\s*\}");
                        if (defining.Success)
                        {
                            function.DefiningType = defining.Groups[1].Value;
                            function.DefiningNumber = int.Parse(defining.Groups[2].Value, CultureInfo.InvariantCulture);
                        }
                    }
                }
                foreach (string type in new[] { "actions", "states", "ranges" })
                {
                    string list = LuaTable.Entries(entry.Value).Where(f => f.Key == "[\"" + type + "\"]").Select(f => f.Value).FirstOrDefault() ?? string.Empty;
                    foreach (Match number in Regex.Matches(list, @"\d+"))
                        function.Members.Add(new KeyValuePair<string, int>(type, int.Parse(number.Value, CultureInfo.InvariantCulture)));
                }
                if (TitlePage.ContainsKey(function.DefiningType))
                    functions[int.Parse(key.Groups[1].Value, CultureInfo.InvariantCulture)] = function;
            }
            return functions;
        }

        // The table constructor that follows the marker ("{ ... }"), or null.
        private static string TableAfter(string script, string marker)
        {
            int at = script.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
                return null;
            int open = script.IndexOf('{', at + marker.Length);
            return open < 0 ? null : LuaTable.Block(script, open);
        }

        private static string Unquote(string text)
        {
            string t = (text ?? string.Empty).Trim();
            if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal))
                t = t.Substring(1, t.Length - 2).Trim();
            return t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"' ? t.Substring(1, t.Length - 2) : t;
        }
    }

    // Just enough of Lua's table constructor syntax for X4's menu tables: entries split at top-level commas, strings and
    // comments skipped. An entry is [key] = value, name = value or a positional value (key null).
    internal static class LuaTable
    {
        // The text from the '{' at open to its matching '}' (both included); null when it does not close.
        public static string Block(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                int skipped = Skip(text, i);
                if (skipped > i)
                {
                    i = skipped - 1;
                    continue;
                }
                if (text[i] == '{')
                    depth++;
                else if (text[i] == '}' && --depth == 0)
                    return text.Substring(open, i - open + 1);
            }
            return null;
        }

        public static List<KeyValuePair<string, string>> Entries(string table)
        {
            List<KeyValuePair<string, string>> entries = new List<KeyValuePair<string, string>>();
            string t = (table ?? string.Empty).Trim();
            if (t.Length < 2 || t[0] != '{' || t[t.Length - 1] != '}')
                return entries;
            int depth = 0;
            StringBuilder current = new StringBuilder();
            for (int i = 1; i < t.Length - 1; i++)
            {
                int skipped = Skip(t, i);
                if (skipped > i)
                {
                    // Strings stay, comments go.
                    if (t[i] == '"' || t[i] == '\'')
                        current.Append(t, i, skipped - i);
                    i = skipped - 1;
                    continue;
                }
                char c = t[i];
                if (c == '{' || c == '(' || c == '[')
                    depth++;
                else if (c == '}' || c == ')' || c == ']')
                    depth--;
                if ((c == ',' || c == ';') && depth == 0)
                {
                    Add(entries, current.ToString());
                    current.Clear();
                    continue;
                }
                current.Append(c);
            }
            Add(entries, current.ToString());
            return entries;
        }

        private static void Add(List<KeyValuePair<string, string>> entries, string entry)
        {
            string text = entry.Trim();
            if (text.Length == 0)
                return;
            Match keyed = Regex.Match(text, @"^(\[[^\]]*\]|[A-Za-z_]\w*)\s*=(?!=)\s*(.*)$", RegexOptions.Singleline);
            if (keyed.Success)
                entries.Add(new KeyValuePair<string, string>(Regex.Replace(keyed.Groups[1].Value, @"\s+", string.Empty), keyed.Groups[2].Value.Trim()));
            else
                entries.Add(new KeyValuePair<string, string>(null, text));
        }

        // The index after a string or comment starting at i, or i.
        private static int Skip(string text, int i)
        {
            char c = text[i];
            if (c == '"' || c == '\'')
            {
                for (int j = i + 1; j < text.Length; j++)
                {
                    if (text[j] == '\\')
                        j++;
                    else if (text[j] == c)
                        return j + 1;
                }
                return text.Length;
            }
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                if (i + 3 < text.Length && text[i + 2] == '[' && text[i + 3] == '[')
                {
                    int end = text.IndexOf("]]", i + 4, StringComparison.Ordinal);
                    return end < 0 ? text.Length : end + 2;
                }
                int line = text.IndexOf('\n', i);
                return line < 0 ? text.Length : line;
            }
            return i;
        }
    }

    // X4's .cat/.dat archives: <NN>.cat is a text index ("<path> <size> <unix time> <md5>", the path may contain
    // spaces), <NN>.dat the files' bytes in index order. A later archive overrides an earlier one; *_sig archives only
    // hold signatures. Only the base archives are read (the extensions do not change the menu or the English texts).
    internal static class X4Archives
    {
        public static List<string> Catalogs(string folder)
        {
            return Directory.GetFiles(folder, "*.cat")
                .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^\d\d\.cat$", RegexOptions.IgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static Dictionary<string, byte[]> ReadFiles(string folder, IEnumerable<string> paths)
        {
            HashSet<string> wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Tuple<string, long, long>> newest = new Dictionary<string, Tuple<string, long, long>>(StringComparer.OrdinalIgnoreCase);
            foreach (string cat in Catalogs(folder))
            {
                string dat = Path.ChangeExtension(cat, ".dat");
                long offset = 0;
                foreach (string raw in File.ReadLines(cat, Encoding.UTF8))
                {
                    string line = raw.TrimEnd('\r', '\n');
                    if (line.Length == 0)
                        continue;
                    string[] tail = line.Split(' ');
                    long size;
                    if (tail.Length < 4 || !long.TryParse(tail[tail.Length - 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out size) || size < 0)
                        throw new InvalidDataException(Path.GetFileName(cat) + " has an unreadable line: " + line);
                    string path = string.Join(" ", tail.Take(tail.Length - 3));
                    if (wanted.Contains(path))
                        newest[path] = Tuple.Create(dat, offset, size);
                    offset += size;
                }
            }
            Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Tuple<string, long, long> at;
                if (!newest.TryGetValue(path, out at))
                    continue;
                byte[] data = new byte[at.Item3];
                using (FileStream stream = new FileStream(at.Item1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    stream.Seek(at.Item2, SeekOrigin.Begin);
                    int read = 0;
                    while (read < data.Length)
                    {
                        int n = stream.Read(data, read, data.Length - read);
                        if (n <= 0)
                            throw new EndOfStreamException(Path.GetFileName(at.Item1) + " ends before " + path + ".");
                        read += n;
                    }
                }
                files[path] = data;
            }
            return files;
        }
    }

    // The {const char* name; int64 value} tables of X4.exe that map INPUT_ACTION_* / INPUT_STATE_* / INPUT_RANGE_* to
    // the numbers the Lua menu uses. Each table is found through a pointer to one known name and walked in both
    // directions while the entries point to names of the same prefix.
    internal static class X4ExeNames
    {
        private static readonly string[][] Anchors =
        {
            new[] { "action", "INPUT_ACTION", "INPUT_ACTION_WIDGET_BACK" },
            new[] { "state", "INPUT_STATE", "INPUT_STATE_BOOST" },
            new[] { "range", "INPUT_RANGE", "INPUT_RANGE_THROTTLE" }
        };

        public static List<Tuple<string, int, string>> Read(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);
            List<Tuple<string, int, string>> controls = new List<Tuple<string, int, string>>();
            int pe = BitConverter.ToInt32(data, 0x3c);
            int sections = BitConverter.ToUInt16(data, pe + 6);
            int optionalSize = BitConverter.ToUInt16(data, pe + 20);
            int optional = pe + 24;
            ulong imageBase = BitConverter.ToUInt64(data, optional + 24);
            List<long[]> table = new List<long[]>();
            for (int i = 0; i < sections; i++)
            {
                int header = optional + optionalSize + i * 40;
                table.Add(new long[] { BitConverter.ToUInt32(data, header + 12), BitConverter.ToUInt32(data, header + 20), BitConverter.ToUInt32(data, header + 16) });
            }
            Func<long, ulong?> toVa = offset =>
            {
                foreach (long[] s in table)
                {
                    if (offset >= s[1] && offset < s[1] + s[2])
                        return imageBase + (ulong)(s[0] + offset - s[1]);
                }
                return null;
            };
            Func<ulong, long> toOffset = va =>
            {
                foreach (long[] s in table)
                {
                    ulong start = imageBase + (ulong)s[0];
                    if (va >= start && va < start + (ulong)s[2])
                        return (long)(va - start) + s[1];
                }
                return -1;
            };

            foreach (string[] anchor in Anchors)
            {
                int text = IndexOf(data, Encoding.ASCII.GetBytes(anchor[2] + "\0"), 0);
                ulong? va = text < 0 ? null : toVa(text);
                int reference = va.HasValue ? IndexOf(data, BitConverter.GetBytes(va.Value), 0) : -1;
                if (reference < 0)
                    continue;
                string prefix = anchor[1] + "_";
                Func<int, KeyValuePair<string, int>?> entry = pos =>
                {
                    if (pos < 0 || pos + 16 > data.Length)
                        return null;
                    long at = toOffset(BitConverter.ToUInt64(data, pos));
                    ulong value = BitConverter.ToUInt64(data, pos + 8);
                    if (at < 0 || value > 100000)
                        return null;
                    int end = (int)at;
                    while (end < data.Length && end - at < 80 && data[end] != 0)
                        end++;
                    string name = Encoding.ASCII.GetString(data, (int)at, end - (int)at);
                    if (!Regex.IsMatch(name, "^INPUT_[A-Z0-9_]+$") || !name.StartsWith(prefix, StringComparison.Ordinal))
                        return null;
                    return new KeyValuePair<string, int>(name, (int)value);
                };
                int first = reference;
                while (entry(first - 16).HasValue)
                    first -= 16;
                for (int pos = first; entry(pos).HasValue; pos += 16)
                {
                    KeyValuePair<string, int> e = entry(pos).Value;
                    if (e.Key.EndsWith("_UNKNOWN", StringComparison.Ordinal) || e.Key.EndsWith("_ENUMSIZE", StringComparison.Ordinal) || e.Key.EndsWith("_MAX", StringComparison.Ordinal))
                        continue;
                    if (!controls.Any(c => c.Item1 == anchor[0] && c.Item3 == e.Key))
                        controls.Add(Tuple.Create(anchor[0], e.Value, e.Key));
                }
            }
            return controls;
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
}
