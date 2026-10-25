using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.DeviceReference;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    // What an action is, in words the user knows (docs/relation-builder.md "Recommended action"), for every list that
    // shows actions (suggestions, search, catalog, relation nodes). Nothing here is written per action:
    //   - the readable name: the game's own title; an MSFS / FSX / P3D event whose list has no menu name gets one from
    //     its event name ("KEY_AXIS_ELEVATOR_SET" -> "Elevator Axis"), any other title that is only its id gets the id's
    //     words;
    //   - what it does: the cockpit guide's "What does it do?" text of the cockpit control the action is (the guide ties
    //     ~28k controls to the games' bindable commands through the cockpit pictures), else the sim's own description
    //     (MSFS en-US.locPak "INPUT.KEY_..._DESC"), else JoyPro's own explanation of the action (DB\Functions\ActionTexts),
    //     else the guide's general explanation of its function
    //     (_templates.json, by the action's own name or its function's name), else nothing. Never invented.
    // The index (command -> text) is built from the guides and picture sets once and kept in <data>\Cache.
    internal static class EngineActionInfo
    {
        private const string Magic = "joypro-action-info 1";

        internal sealed class Info
        {
            public string Readable = string.Empty;
            public string Explanation = string.Empty;
            // "guide" (a cockpit guide control), "game" (the sim's own description), "template" (the general explanation
            // of its function) or empty.
            public string Source = string.Empty;
        }

        private static readonly object Sync = new object();
        private static Dictionary<string, KeyValuePair<string, string>> index;
        private static CockpitGuideTemplates templates;
        private static bool templatesLoaded;

        private static readonly HashSet<string> EventGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MSFS2020", "MSFS2024", "FSX", "FSXSE", "P3D4", "P3D5", "P3D6"
        };

        // Tests: a fixed index instead of the guides on disk.
        internal static void SetIndex(Dictionary<string, KeyValuePair<string, string>> entries)
        {
            lock (Sync)
                index = entries;
        }

        internal static void Reset()
        {
            lock (Sync)
            {
                index = null;
                templates = null;
                templatesLoaded = false;
            }
        }

        public static string CacheFile()
        {
            return Path.Combine(GameInstalls.DataRoot(), "Cache", "action-info.tsv");
        }

        // The title to show. The game's own words stay; JoyPro's own name (DB\Functions\ActionTexts) stands in for a title
        // that is only the id or a string-table key; an event name or an id shown as its title is made readable; shorthand
        // of the game's words is written out (ActionTexts.ExpandAbbreviations, not for DCS).
        public static string Readable(string game, string id, string title)
        {
            string own = EngineActionTexts.Title(game, id, title);
            if (!string.Equals(own, title ?? string.Empty, StringComparison.Ordinal))
                return own;
            return ActionTexts.ExpandAbbreviations(game, ReadableCore(game, id, title));
        }

        private static string ReadableCore(string game, string id, string title)
        {
            string t = (title ?? string.Empty).Trim();
            string i = (id ?? string.Empty).Trim();
            if (EventGames.Contains(game ?? string.Empty) && i.Length > 0 &&
                (t.Length == 0 || string.Equals(t, i, StringComparison.OrdinalIgnoreCase) || string.Equals(t, MsfsGameData.RawTitle(i), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(t.Replace(' ', '_'), i.StartsWith("KEY_", StringComparison.Ordinal) ? i.Substring(4) : i, StringComparison.OrdinalIgnoreCase)))
                return MsfsGameData.TitleFromEventName(i.StartsWith("KEY_", StringComparison.Ordinal) ? i : "KEY_" + i);
            // Star Citizen's lists name an action by its id ("v_toggle_landing_system" shown as "v toggle landing system"):
            // its words without the vehicle prefix ("Toggle landing system"), EVA as EVA.
            if (string.Equals(game, "StarCitizen", StringComparison.OrdinalIgnoreCase) && i.Length > 0)
            {
                string action = i.Substring(i.LastIndexOf('|') + 1);
                int split = action.LastIndexOf("___", StringComparison.Ordinal);
                if (split >= 0)
                    action = action.Substring(split + 3);
                if (t.Length == 0 || string.Equals(t, action.Replace('_', ' '), StringComparison.OrdinalIgnoreCase) || string.Equals(t, action, StringComparison.OrdinalIgnoreCase))
                {
                    List<string> parts = action.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    if (parts.Count > 1 && parts[0] == "v")
                        parts.RemoveAt(0);
                    if (parts.Count > 0)
                    {
                        string text = string.Join(" ", parts.Select(p => p == "eva" ? "EVA" : p == "ifcs" ? "IFCS" : p == "esp" ? "ESP" : p == "mfd" ? "MFD" : p));
                        return char.ToUpperInvariant(text[0]) + text.Substring(1);
                    }
                }
            }
            // An id shown as its title, also behind a key prefix of the game's string table (IL-2 "inpmc_pitch" for
            // "mc_pitch").
            bool idTitle = i.Length > 0 && Regex.IsMatch(t, @"^[A-Za-z0-9]+([_.][A-Za-z0-9]+)+$") && t.EndsWith(i, StringComparison.OrdinalIgnoreCase);
            // (An id that is already words, "A/P Spd Hold" in VTOL VR, is no program name.)
            if (t.Length == 0 || idTitle || (string.Equals(t, i, StringComparison.Ordinal) && i.IndexOf(' ') < 0 && Regex.IsMatch(i, "[_./]|[a-z][A-Z]")))
            {
                string[] words = Regex.Split(Regex.Replace(i, "([a-z])([A-Z])", "$1 $2"), @"[_./\s]+").Where(w => w.Length > 0).ToArray();
                if (words.Length == 0)
                    return t;
                string text = string.Join(" ", words).ToLowerInvariant();
                return char.ToUpperInvariant(text[0]) + text.Substring(1);
            }
            return t;
        }

        // A general explanation in the game's words: the template says "aircraft"; Star Citizen, Elite Dangerous and X4 fly
        // ships, a tank or vehicle action (War Thunder, Arma, Battlefield: by its category) drives one.
        public static string InGameWords(string game, string category, string text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? string.Empty;
            string cat = (category ?? string.Empty).ToLowerInvariant();
            string noun = EngineGameWords.SpaceGames.Contains(game ?? string.Empty) ? "ship"
                : Regex.IsMatch(cat, @"\btanks?\b|\barmou?r") ? "tank"
                : Regex.IsMatch(cat, @"\b(?:vehicles?|cars?|ground|driving|wheeled|tracked|boats?|ships?|naval)\b") ? (Regex.IsMatch(cat, @"\b(?:boats?|ships?|naval)\b") ? "ship" : "vehicle")
                : string.Empty;
            if (noun.Length == 0)
                return text;
            string result = Regex.Replace(text, @"\baircraft\b", noun);
            result = Regex.Replace(result, @"\bAircraft\b", char.ToUpperInvariant(noun[0]) + noun.Substring(1));
            result = Regex.Replace(result, @"\b(?:aeroplane|airplane|plane)\b", noun);
            return result;
        }

        // Is the title the game's own readable name (not an id or an event name)?
        public static bool IsReadable(string game, string id, string title)
        {
            // A title that is its id is fine when the id is words ("Joystick Pitch", "Throttle"); an event name
            // ("ADF 1 DEC" for KEY_ADF_1_DEC), "spaceship_movement" or "PitchAxisRaw" is not.
            string t = (title ?? string.Empty).Trim();
            if (t.Length == 0)
                return false;
            if (EventGames.Contains(game ?? string.Empty) && string.Equals(t, MsfsGameData.RawTitle(id ?? string.Empty), StringComparison.Ordinal))
                return false;
            return !Regex.IsMatch(t, @"^[A-Za-z0-9]+([_.][A-Za-z0-9]+)+$") && !Regex.IsMatch(t, @"^[a-z]+[A-Z][A-Za-z0-9]*$|^[A-Z][a-z]+[A-Z][A-Za-z0-9]*$");
        }

        // Readable name and explanation of one action. plane: the DCS aircraft (other games: the game); function: the
        // action's own function of the function map (its id and name), for the general explanation.
        // general: the function's general explanation may be used (false for a variant that is something else: a view,
        // a weapon, a word the game does not explain).
        public static Info Describe(string game, string plane, string id, string title, string functionId, string functionName, bool general = true)
        {
            Info info = new Info { Readable = Readable(game, id, title) };
            KeyValuePair<string, string> hit;
            Dictionary<string, KeyValuePair<string, string>> map = Index();
            string g = EngineRelationSuggest.GameKey(game ?? string.Empty);
            if ((g == "DCS" && map.TryGetValue(Key(g, plane, id), out hit)) || map.TryGetValue(Key(g, string.Empty, id), out hit))
            {
                info.Source = hit.Key;
                info.Explanation = hit.Value;
                return info;
            }
            // JoyPro's own explanation of this very action (DB\Functions\ActionTexts: every IL-2 action, and the ones
            // written for other games).
            ActionTexts.Entry own = EngineActionTexts.Find(game, id);
            if (own != null && own.Explanation.Length > 0)
            {
                info.Source = "joypro";
                info.Explanation = own.Explanation;
                return info;
            }
            CockpitGuideTemplates t = general ? Templates() : null;
            if (t != null)
            {
                // By the function, never by the action's own words ("pitch" is also the propeller's pitch).
                string templateId = EngineActionChoice.TemplateOf(functionId);
                CockpitGuideTemplates.Template found = templateId != null ? (templateId.Length > 0 ? t.Items.FirstOrDefault(x => x.Id == templateId) : null)
                    : string.IsNullOrEmpty(functionName) || string.IsNullOrEmpty(functionId) ? null : t.Find(functionName);
                if (found != null && found.Text.Length > 0)
                {
                    info.Source = "template";
                    info.Explanation = found.Text.Replace("{name}", info.Readable);
                }
            }
            return info;
        }

        // relations action-info --coverage: per game how many actions have a readable name (the game's own, or made
        // readable from an id / event name), an explanation (cockpit guide, the game's own description, the general one
        // of its function, none) and how many of the game's functions have a recommended action that is no variant.
        internal static EngineActionCoverage Coverage(string onlyGame, int examples = 6)
        {
            EngineActionCoverage result = new EngineActionCoverage();
            EngineFunctionMap.Map map = EngineFunctionMap.Load(result.Warnings);
            List<EngineRelationSuggest.Command> commands = EngineRelationSuggest.Index(result.Warnings);
            string only = string.IsNullOrWhiteSpace(onlyGame) ? string.Empty : EngineRelationSuggest.GameKey(onlyGame);
            foreach (IGrouping<string, EngineRelationSuggest.Command> game in commands.Where(c => only.Length == 0 || string.Equals(c.Game, only, StringComparison.OrdinalIgnoreCase))
                         .GroupBy(c => c.Game, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                EngineActionCoverageRow row = new EngineActionCoverageRow { Game = game.Key };
                List<EngineActionChoice.Item> items = new List<EngineActionChoice.Item>();
                Dictionary<string, HashSet<string>> words = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (EngineRelationSuggest.Command c in game)
                {
                    row.Actions++;
                    if (IsReadable(c.Game, c.Id, c.Title))
                        row.NamedByGame++;
                    string readable = Readable(c.Game, c.Id, c.Title);
                    if (IsReadable(c.Game, c.Id, readable))
                        row.Readable++;
                    string abbreviation = ActionTexts.UnexplainedAbbreviation(c.Game, c.Id, readable);
                    if (abbreviation != null)
                    {
                        row.Abbreviated++;
                        if (row.AbbreviatedExamples.Count < examples)
                            row.AbbreviatedExamples.Add(c.Id + " \"" + readable + "\" (" + abbreviation + ")");
                    }
                    if (ActionTexts.IsWordPlusAxis(readable))
                    {
                        row.WordAxis++;
                        if (row.WordAxisExamples.Count < Math.Min(examples, 4) || examples == int.MaxValue)
                            row.WordAxisExamples.Add(c.Id + " \"" + readable + "\"");
                    }
                    EngineFunctionMap.Entry entry = null;
                    EngineFunctionMap.Assignment top = map != null && map.ByKey.TryGetValue(c.Game + "|" + c.Id + "|" + c.Plane, out entry)
                        ? entry.Functions.Where(a => a.Weight >= 0.9).OrderByDescending(a => a.Weight).FirstOrDefault() : null;
                    Info info = Describe(c.Game, c.Plane, c.Id, c.Title, top != null ? top.Function.Id : string.Empty, top != null ? top.Function.Name : string.Empty);
                    if (info.Source == "guide")
                        row.Guide++;
                    else if (info.Source == "game")
                        row.GameText++;
                    else if (info.Source == "template")
                        row.Template++;
                    else if (info.Source == "joypro")
                        row.JoyProText++;
                    if (top == null)
                        continue;
                    HashSet<string> known;
                    if (!words.TryGetValue(top.Function.Id, out known))
                        words[top.Function.Id] = known = EngineActionChoice.WordsOf(top.Function);
                    items.Add(new EngineActionChoice.Item
                    {
                        Game = c.Game, Id = c.Id, Title = readable, Category = c.Category, Planes = new List<string> { c.Plane }, Function = top.Function.Id + "|" + (c.IsAxis ? "a" : "b"),
                        Known = known, PrimaryRank = EngineFunctionMap.PrimaryRank(entry, top.Function)
                    });
                }
                EngineActionChoice.Mark(items);
                // A function (with its kind) of the game, per DCS aircraft: has it a recommended action that is no variant?
                foreach (IGrouping<string, EngineActionChoice.Item> function in items.GroupBy(i => i.Function + "|" + i.Planes[0], StringComparer.OrdinalIgnoreCase))
                {
                    row.Functions++;
                    if (function.Any(i => i.Recommended && i.FitsFunction && i.Unknown.Count == 0 && !i.Rules.Any(r => r.Demote)))
                        row.FunctionsRecommended++;
                }
                result.Games.Add(row);
            }
            return result;
        }

        internal static string ToText(EngineActionCoverage result)
        {
            StringBuilder sb = new StringBuilder();
            Func<int, int, string> pct = (n, of) => of == 0 ? "-" : ((int)Math.Round(100.0 * n / of)).ToString(CultureInfo.InvariantCulture) + "%";
            sb.AppendLine("Action names and explanations per game (readable = the game's own name or one made from its id; explained by the cockpit guide / the game's own description / the general text of its function; functions with a recommended action that is no variant)");
            foreach (EngineActionCoverageRow r in result.Games)
            {
                int explained = r.Guide + r.GameText + r.JoyProText + r.Template;
                sb.AppendLine("  " + r.Game + " | actions " + r.Actions.ToString(CultureInfo.InvariantCulture) + " | named by the game " + pct(r.NamedByGame, r.Actions) + ", readable " + pct(r.Readable, r.Actions) +
                              " | unexplained abbreviations " + r.Abbreviated.ToString(CultureInfo.InvariantCulture) + ", word+axis " + r.WordAxis.ToString(CultureInfo.InvariantCulture) +
                              " | explained " + pct(explained, r.Actions) + " (guide " + pct(r.Guide, r.Actions) + ", game " + pct(r.GameText, r.Actions) + ", JoyPro " + pct(r.JoyProText, r.Actions) + ", general " + pct(r.Template, r.Actions) + ", none " + pct(r.Actions - explained, r.Actions) + ")" +
                              " | functions " + r.Functions.ToString(CultureInfo.InvariantCulture) + ", recommended " + pct(r.FunctionsRecommended, r.Functions));
                foreach (string example in r.AbbreviatedExamples)
                    sb.AppendLine("      abbreviation: " + example);
                foreach (string example in r.WordAxisExamples)
                    sb.AppendLine("      word+axis: " + example);
            }
            foreach (string warning in result.Warnings.Distinct().Take(10))
                sb.AppendLine("Warning: " + warning);
            return sb.ToString();
        }

        private static string Key(string game, string plane, string id)
        {
            return game + "|" + (plane ?? string.Empty) + "|" + NormalId(game, id);
        }

        // MSFS events with or without "KEY_"; Star Citizen "map|action" as "map___action".
        private static string NormalId(string game, string id)
        {
            string i = EngineRelationSuggest.NodeId(game, id ?? string.Empty);
            if (EventGames.Contains(game) && i.StartsWith("KEY_", StringComparison.OrdinalIgnoreCase))
                i = i.Substring(4);
            return i.ToUpperInvariant();
        }

        private static CockpitGuideTemplates Templates()
        {
            lock (Sync)
            {
                if (!templatesLoaded)
                {
                    templatesLoaded = true;
                    try
                    {
                        CockpitCatalog catalog = Program.LoadCockpitCatalog();
                        templates = catalog == null ? null : CockpitGuideLibrary.For(catalog.Folder).Templates("en");
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException)
                    {
                        templates = null;
                    }
                }
                return templates;
            }
        }

        private static Dictionary<string, KeyValuePair<string, string>> Index()
        {
            lock (Sync)
            {
                if (index != null)
                    return index;
                index = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    CockpitCatalog catalog = Program.LoadCockpitCatalog();
                    if (catalog == null)
                        return index;
                    string fingerprint = Fingerprint(catalog);
                    if (TryRead(fingerprint, index))
                        return index;
                    Build(catalog, index);
                    Write(fingerprint, index);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException)
                {
                    JoyPro.Logging.JoyProLog.Warn("ActionInfo", "Could not build the action explanations: " + ex.Message);
                }
                return index;
            }
        }

        // Every guide control with its text, by the game commands it is (the variant's own guide before the shared ones
        // of its game: the first text found for a command stays), then the sims' own descriptions.
        private static void Build(CockpitCatalog catalog, Dictionary<string, KeyValuePair<string, string>> map)
        {
            CockpitGuideLibrary library = CockpitGuideLibrary.For(catalog.Folder);
            foreach (CockpitAircraft a in catalog.Listed)
            {
                foreach (CockpitVariant raw in a.Variants)
                {
                    CockpitVariant v = catalog.Resolve(raw);
                    CockpitPictureSet pictures = CockpitPictureSet.For(v);
                    CockpitGuideTree tree = CockpitGuideTree.Build(catalog, v, pictures, library, "en");
                    foreach (CockpitGuideNode node in tree.All().Where(n => n.Kind == "control" && n.Text.Length > 0 && n.Template.Length == 0 && n.Commands.Count > 0))
                    {
                        string text = Flat(node.Text);
                        foreach (CockpitGameRef game in v.Games)
                        {
                            string g = EngineRelationSuggest.GameKey(game.Game);
                            foreach (CockpitPictureCommand c in node.Commands.Where(c => !string.IsNullOrEmpty(c.Id)))
                            {
                                string key = Key(g, g == "DCS" ? game.Plane : string.Empty, c.Id);
                                if (!map.ContainsKey(key))
                                    map[key] = new KeyValuePair<string, string>("guide", text);
                            }
                        }
                    }
                }
            }
            foreach (int sim in new[] { 2020, 2024 })
            {
                List<string> warnings = new List<string>();
                List<string> roots = MsfsGameData.Roots(sim, warnings);
                string g = sim == 2020 ? "MSFS2020" : "MSFS2024";
                foreach (KeyValuePair<string, string> d in MsfsGameData.Descriptions(roots, warnings))
                {
                    string key = Key(g, string.Empty, d.Key);
                    if (!map.ContainsKey(key))
                        map[key] = new KeyValuePair<string, string>("game", Flat(d.Value));
                }
            }
        }

        private static string Flat(string text)
        {
            return Regex.Replace(text ?? string.Empty, @"[\t\r\n]+", " ").Trim();
        }

        private static string Fingerprint(CockpitCatalog catalog)
        {
            StringBuilder sb = new StringBuilder(Magic).Append('\n');
            List<string> files = new List<string> { typeof(EngineActionInfo).Assembly.Location };
            string guide = CockpitGuideLibrary.FolderOf(catalog.Folder);
            if (Directory.Exists(guide))
                files.AddRange(Directory.GetFiles(guide, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            if (Directory.Exists(catalog.Folder))
                files.AddRange(Directory.GetFiles(catalog.Folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            string pictures = CockpitPictureSet.Root();
            if (Directory.Exists(pictures))
                files.AddRange(Directory.GetFiles(pictures, "*.json", SearchOption.AllDirectories).Where(f => f.EndsWith("pictures.json", StringComparison.OrdinalIgnoreCase) || f.EndsWith("index.json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            foreach (int sim in new[] { 2020, 2024 })
            {
                foreach (string root in MsfsGameData.Roots(sim, new List<string>()))
                {
                    files.Add(Path.Combine(root, "Packages", "fs-base", "en-US.locPak"));
                    files.Add(Path.Combine(root, "fs-base", "en-US.locPak"));
                }
            }
            foreach (string file in files)
            {
                FileInfo info = new FileInfo(file);
                sb.Append(file).Append('|');
                if (info.Exists)
                    sb.Append(info.Length.ToString(CultureInfo.InvariantCulture)).Append('|').Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                sb.Append('\n');
            }
            return Convert.ToBase64String(System.Security.Cryptography.SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        private static bool TryRead(string fingerprint, Dictionary<string, KeyValuePair<string, string>> map)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("JOYPRO_FUNCTION_MAP_CACHE"), "off", StringComparison.OrdinalIgnoreCase))
                return false;
            string file = CacheFile();
            if (!File.Exists(file))
                return false;
            string[] lines = File.ReadAllLines(file, Encoding.UTF8);
            if (lines.Length == 0 || lines[0] != fingerprint)
                return false;
            for (int i = 1; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split('\t');
                if (parts.Length == 3)
                    map[parts[0]] = new KeyValuePair<string, string>(parts[1], parts[2]);
            }
            return true;
        }

        private static void Write(string fingerprint, Dictionary<string, KeyValuePair<string, string>> map)
        {
            try
            {
                string file = CacheFile();
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                List<string> lines = new List<string> { fingerprint };
                lines.AddRange(map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "\t" + p.Value.Key + "\t" + p.Value.Value));
                string temp = file + ".tmp";
                File.WriteAllLines(temp, lines, new UTF8Encoding(false));
                if (File.Exists(file))
                    File.Delete(file);
                File.Move(temp, file);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                JoyPro.Logging.JoyProLog.Warn("ActionInfo", "Could not write the action explanations cache: " + ex.Message);
            }
        }
    }

    public sealed class EngineActionCoverage
    {
        public List<EngineActionCoverageRow> Games { get; set; }
        public List<string> Warnings { get; set; }

        public EngineActionCoverage()
        {
            Games = new List<EngineActionCoverageRow>();
            Warnings = new List<string>();
        }
    }

    // One game (DCS: every aircraft's actions together; functions counted per aircraft).
    public sealed class EngineActionCoverageRow
    {
        public string Game { get; set; }
        public int Actions { get; set; }
        public int NamedByGame { get; set; }
        public int Readable { get; set; }
        public int Guide { get; set; }
        public int GameText { get; set; }
        public int Template { get; set; }
        // JoyPro's own explanation of the action (DB\Functions\ActionTexts).
        public int JoyProText { get; set; }
        // Titles with a word nobody can read (ActionTexts.UnexplainedAbbreviation) / only a word and an axis letter.
        public int Abbreviated { get; set; }
        public int WordAxis { get; set; }
        public List<string> AbbreviatedExamples { get; set; }
        public List<string> WordAxisExamples { get; set; }
        public int Functions { get; set; }
        public int FunctionsRecommended { get; set; }

        public EngineActionCoverageRow()
        {
            Game = string.Empty;
            AbbreviatedExamples = new List<string>();
            WordAxisExamples = new List<string>();
        }
    }
}
