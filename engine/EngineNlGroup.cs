using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    // "Group by description" (engine relations from-text): the user writes in English what belongs together, e.g.
    // "speed brake in and out on all jets", and JoyPro builds the relations from the function catalog (DB\Functions).
    // The offline rules always run: a deterministic parser with the words of DB\Functions\language.json (functions
    // and their directions, games, aircraft classes, eras, roles and aircraft names) picks the function slots and the
    // aircraft, the catalog gives every aircraft's command for the slot. An optional local language model
    // (EngineLlm, only when the user enabled and downloaded it) may re-pick functions and aircraft from a candidate
    // list the offline matcher built; every id it answers is checked against that list and the catalogs.
    internal static class EngineNlGroup
    {
        public const string GroupTag = "FromText";
        public static readonly string[] LlmModes = { "off", "auto", "on" };
        private const double PartialGameFactor = 0.6;
        private const double RetrievalFactor = 0.6;
        private const double LlmFactor = 0.85;

        internal sealed class Options
        {
            public string Request = string.Empty;
            public List<string> Games = new List<string>();
            public List<string> Planes = new List<string>();
            public bool DryRun;
            public string LlmMode = string.Empty;
            public bool Substitutes;
            public List<string> Select = new List<string>();
            public List<string> SkipNodes = new List<string>();
            public string Name = string.Empty;
            public double MinConfidence = 0.5;
            // --show-prompt: put the local model's prompt and grammar into the result (no model runs).
            public bool ShowPrompt;
            public string PromptModel = string.Empty;
        }

        // ------------------------------------------------------------------------------------------------------
        // Vocabulary (DB\Functions\language.json)
        // ------------------------------------------------------------------------------------------------------

        internal sealed class Variant
        {
            public string Key = string.Empty;
            public string Label = string.Empty;
            public List<string> Slots = new List<string>();
            public List<string> Terms = new List<string>();
            public bool Default = true;
        }

        internal sealed class Concept
        {
            public string Id = string.Empty;
            public string Label = string.Empty;
            public List<string> Terms = new List<string>();
            public List<Variant> Variants = new List<Variant>();
            // language.json function_info: the cockpit system, what the function does, close relatives.
            public string System = string.Empty;
            public string About = string.Empty;
            public List<string> Related = new List<string>();
        }

        internal sealed class GameWord
        {
            public string Id = string.Empty;
            public string Label = string.Empty;
            public List<string> Terms = new List<string>();
            // A word for several games (language.json "members": "MSFS" = MSFS 2024 and MSFS 2020, "X-Plane" = 12 and 11).
            public List<string> Members = new List<string>();

            public bool Covers(string game)
            {
                return string.Equals(Id, game, StringComparison.OrdinalIgnoreCase) || Members.Contains(game ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            }
        }

        internal sealed class CategoryWord
        {
            public string Id = string.Empty;
            public string Label = string.Empty;
            public string Dimension = string.Empty;
            public List<string> Terms = new List<string>();
            public HashSet<string> Tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Eras = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        internal sealed class GameProfile
        {
            public bool Mixed;
            public HashSet<string> Tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Eras = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        internal enum HitKind
        {
            Function = 0,
            Game = 1,
            Category = 2,
            Aircraft = 3
        }

        internal sealed class Hit
        {
            public HitKind Kind;
            public Concept Concept;
            public GameWord Game;
            public CategoryWord Category;
            public List<string> Aircraft = new List<string>();
            public string Phrase = string.Empty;
        }

        internal sealed class Vocabulary
        {
            public HashSet<string> Stopwords = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> Prepositions = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> Quantifiers = new HashSet<string>(StringComparer.Ordinal);
            public List<string> Exclusions = new List<string>();
            public List<Concept> Concepts = new List<Concept>();
            public List<GameWord> Games = new List<GameWord>();
            public List<CategoryWord> Categories = new List<CategoryWord>();
            public Dictionary<string, List<string>> Aliases = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            public Dictionary<string, List<string>> Nicknames = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<string>> Tags = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, GameProfile> Profiles = new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, Hit> Index = new Dictionary<string, Hit>(StringComparer.Ordinal);
            public int MaxPhrase = 1;
            public List<string> Problems = new List<string>();
        }

        // The function catalog the parser resolves against (EngineAutoGroup's loader).
        internal sealed class Catalog
        {
            public Dictionary<string, EngineAutoGroup.Slot> Slots;
            public Dictionary<string, EngineAutoGroup.Aircraft> Aircraft;
            public List<EngineAutoGroup.Row> Rows;
            public Dictionary<string, List<RowInfo>> RowsBySlot = new Dictionary<string, List<RowInfo>>(StringComparer.OrdinalIgnoreCase);
        }

        internal sealed class RowInfo
        {
            public EngineAutoGroup.Row Row;
            public string Control = string.Empty;
        }

        // ------------------------------------------------------------------------------------------------------
        // Text normalisation
        // ------------------------------------------------------------------------------------------------------

        private static readonly Dictionary<string, string> Irregular = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "axes", "axis" },
            { "heavies", "heavy" },
            { "helis", "heli" },
            { "nods", "nods" },
            { "chaffs", "chaff" },
            { "lbs", "lb" },
            { "has", "has" },
            { "was", "was" },
            { "does", "does" },
            { "goes", "goes" },
            { "always", "always" },
            { "series", "series" },
            { "gunships", "gunship" }
        };

        private static readonly Regex SlashWords = new Regex(@"(?<![a-z0-9])(f|a)\s*/\s*(a|g)(?![a-z])", RegexOptions.CultureInvariant);

        // Lower case, punctuation to spaces, letter/digit boundaries split ("f16" = "f-16" = "f 16"), "&", ",", "/",
        // "+" and ";" become "and", plural s dropped. The vocabulary goes through the same function.
        internal static List<string> Tokenize(string text)
        {
            StringBuilder sb = new StringBuilder();
            char prev = ' ';
            bool prevAlnum = false;
            // A slash inside a designation or mode is not "and": "F/A-18" = "FA-18", "A/G" = "A2G", "A/A" = "A2A".
            string value = SlashWords.Replace((text ?? string.Empty).ToLowerInvariant(), m => m.Groups[1].Value == "a" ? "a2" + m.Groups[2].Value : m.Groups[2].Value == "a" ? "fa" : m.Value);
            foreach (char raw in value)
            {
                char c = raw;
                if (char.IsLetterOrDigit(c))
                {
                    if (prevAlnum && char.IsDigit(c) != char.IsDigit(prev))
                        sb.Append(' ');
                    sb.Append(c);
                    prevAlnum = true;
                    prev = c;
                    continue;
                }
                if (c == '&' || c == ',' || c == '/' || c == '+' || c == ';')
                    sb.Append(" and ");
                else
                    sb.Append(' ');
                prevAlnum = false;
                prev = ' ';
            }
            List<string> tokens = new List<string>();
            foreach (string token in sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                tokens.Add(Stem(token));
            return tokens;
        }

        private static string Stem(string token)
        {
            string irregular;
            if (Irregular.TryGetValue(token, out irregular))
                return irregular;
            if (token.Length > 4 && token.EndsWith("ies", StringComparison.Ordinal))
                return token.Substring(0, token.Length - 3) + "y";
            if (token.Length > 3 && token.EndsWith("s", StringComparison.Ordinal) &&
                !token.EndsWith("ss", StringComparison.Ordinal) && !token.EndsWith("us", StringComparison.Ordinal) &&
                !token.EndsWith("is", StringComparison.Ordinal) && !char.IsDigit(token[token.Length - 2]))
                return token.Substring(0, token.Length - 1);
            return token;
        }

        internal static string Phrase(string text)
        {
            return string.Join(" ", Tokenize(text));
        }

        // ------------------------------------------------------------------------------------------------------
        // Loading
        // ------------------------------------------------------------------------------------------------------

        // JoyPro's function catalog and map (DB\Functions of the program folder); JOYPRO_FUNCTIONS_DIR replaces it, for
        // tests on fixtures only.
        internal static string FunctionsFolder()
        {
            string over = Environment.GetEnvironmentVariable("JOYPRO_FUNCTIONS_DIR");
            if (!string.IsNullOrWhiteSpace(over))
                return over.Trim();
            return Path.Combine(MainStructure.PROGPATH ?? string.Empty, "DB", "Functions");
        }

        internal static bool TryLoad(List<string> errors, out Vocabulary vocabulary, out Catalog catalog)
        {
            vocabulary = null;
            catalog = null;
            string folder = FunctionsFolder();
            EngineAutoGroupResult probe = new EngineAutoGroupResult();
            Dictionary<string, EngineAutoGroup.Slot> slots;
            Dictionary<string, EngineAutoGroup.Aircraft> aircraft;
            List<EngineAutoGroup.Row> rows;
            if (!EngineAutoGroup.TryLoadCatalog(folder, probe, out slots, out aircraft, out rows))
            {
                errors.AddRange(probe.Errors);
                return false;
            }
            catalog = new Catalog { Slots = slots, Aircraft = aircraft, Rows = rows };
            Dictionary<string, Dictionary<string, string>> controls = LoadControls(folder);
            foreach (EngineAutoGroup.Row row in rows)
            {
                List<RowInfo> list;
                if (!catalog.RowsBySlot.TryGetValue(row.Slot, out list))
                    catalog.RowsBySlot[row.Slot] = list = new List<RowInfo>();
                Dictionary<string, string> byKey;
                string control = string.Empty;
                if (controls.TryGetValue(row.Game, out byKey))
                    byKey.TryGetValue(row.Plane + "\t" + row.Slot + "\t" + row.Action, out control);
                list.Add(new RowInfo { Row = row, Control = control ?? string.Empty });
            }

            string file = Path.Combine(folder, "language.json");
            if (!File.Exists(file))
            {
                errors.Add("Language file missing: " + file + " (update JoyPro).");
                return false;
            }
            try
            {
                vocabulary = ParseVocabulary(File.ReadAllText(file, Encoding.UTF8), catalog);
            }
            catch (Exception ex)
            {
                errors.Add("Language file could not be read: " + ex.Message);
                return false;
            }
            return true;
        }

        // The 7th column of the catalog (what the control does in the cockpit) explains each command.
        private static Dictionary<string, Dictionary<string, string>> LoadControls(string folder)
        {
            Dictionary<string, Dictionary<string, string>> result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(folder, "*.tsv"))
            {
                if (file.EndsWith(".rejected.tsv", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(file), "slot_controls.tsv", StringComparison.OrdinalIgnoreCase))
                    continue;
                Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    if (line.Length == 0 || line[0] == '#')
                        continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length >= 7)
                        map[parts[0] + "\t" + parts[1] + "\t" + parts[2]] = parts[6].Trim();
                }
                result[Path.GetFileNameWithoutExtension(file)] = map;
            }
            return result;
        }

        internal static Vocabulary ParseVocabulary(string json, Catalog catalog)
        {
            Vocabulary v = new Vocabulary();
            Dictionary<string, object> root = EngineSerialization.FromJson<Dictionary<string, object>>(json);
            foreach (string word in Strings(Get(root, "stopwords")))
                v.Stopwords.Add(Phrase(word));
            foreach (string word in Strings(Get(root, "prepositions")))
                v.Prepositions.Add(Phrase(word));
            foreach (string word in Strings(Get(root, "quantifiers")))
                v.Quantifiers.Add(Phrase(word));
            foreach (string word in Strings(Get(root, "exclusions")))
                v.Exclusions.Add(Phrase(word));

            foreach (Dictionary<string, object> item in Objects(Get(root, "functions")))
            {
                Concept concept = new Concept { Id = Text(item, "id"), Label = Text(item, "label") };
                concept.Terms = Strings(Get(item, "terms")).Select(Phrase).Where(t => t.Length > 0).Distinct().ToList();
                foreach (Dictionary<string, object> variantItem in Objects(Get(item, "variants")))
                {
                    Variant variant = new Variant
                    {
                        Key = Text(variantItem, "key"),
                        Label = Text(variantItem, "label"),
                        Slots = Strings(Get(variantItem, "slots")).ToList(),
                        Terms = Strings(Get(variantItem, "terms")).Select(Phrase).Where(t => t.Length > 0).Distinct().ToList(),
                        Default = !string.Equals(Text(variantItem, "default"), "false", StringComparison.OrdinalIgnoreCase)
                    };
                    foreach (string slot in variant.Slots)
                    {
                        if (catalog != null && !catalog.Slots.ContainsKey(slot))
                            v.Problems.Add("Function '" + concept.Id + "' variant '" + variant.Key + "' names unknown slot '" + slot + "'.");
                    }
                    concept.Variants.Add(variant);
                }
                v.Concepts.Add(concept);
                foreach (string term in concept.Terms)
                    AddHit(v, term, new Hit { Kind = HitKind.Function, Concept = concept, Phrase = term });
            }
            Dictionary<string, object> functionInfo = Get(root, "function_info") as Dictionary<string, object>;
            if (functionInfo != null)
            {
                foreach (KeyValuePair<string, object> pair in functionInfo)
                {
                    Concept concept = v.Concepts.FirstOrDefault(c => c.Id == pair.Key);
                    Dictionary<string, object> item = pair.Value as Dictionary<string, object>;
                    if (concept == null || item == null)
                    {
                        v.Problems.Add("function_info names unknown function '" + pair.Key + "'.");
                        continue;
                    }
                    concept.System = Text(item, "system");
                    concept.About = Text(item, "about");
                    concept.Related = Strings(Get(item, "related")).ToList();
                }
                foreach (Concept concept in v.Concepts)
                {
                    foreach (string related in concept.Related.Where(r => !v.Concepts.Any(c => c.Id == r)))
                        v.Problems.Add("Function '" + concept.Id + "' names unknown related function '" + related + "'.");
                }
            }

            foreach (Dictionary<string, object> item in Objects(Get(root, "games")))
            {
                GameWord game = new GameWord { Id = Text(item, "id"), Label = Text(item, "label") };
                game.Terms = Strings(Get(item, "terms")).Select(Phrase).Where(t => t.Length > 0).Distinct().ToList();
                game.Members = Strings(Get(item, "members")).Where(m => m.Length > 0).ToList();
                v.Games.Add(game);
                foreach (string term in game.Terms)
                    AddHit(v, term, new Hit { Kind = HitKind.Game, Game = game, Phrase = term });
            }

            foreach (Dictionary<string, object> item in Objects(Get(root, "categories")))
            {
                CategoryWord category = new CategoryWord { Id = Text(item, "id"), Label = Text(item, "label"), Dimension = Text(item, "dimension") };
                category.Terms = Strings(Get(item, "terms")).Select(Phrase).Where(t => t.Length > 0).Distinct().ToList();
                Dictionary<string, object> match = Get(item, "match") as Dictionary<string, object>;
                category.Tags.UnionWith(Strings(Get(match, "tags")));
                category.Types.UnionWith(Strings(Get(match, "types")));
                category.Roles.UnionWith(Strings(Get(match, "roles")));
                category.Eras.UnionWith(Strings(Get(match, "eras")));
                v.Categories.Add(category);
                foreach (string term in category.Terms)
                    AddHit(v, term, new Hit { Kind = HitKind.Category, Category = category, Phrase = term });
            }

            Dictionary<string, object> tags = Get(root, "aircraft_tags") as Dictionary<string, object>;
            if (tags != null)
            {
                foreach (KeyValuePair<string, object> pair in tags)
                    v.Tags[pair.Key] = new HashSet<string>(Strings(pair.Value), StringComparer.OrdinalIgnoreCase);
            }
            Dictionary<string, object> profiles = Get(root, "game_profiles") as Dictionary<string, object>;
            if (profiles != null)
            {
                foreach (KeyValuePair<string, object> pair in profiles)
                {
                    Dictionary<string, object> item = pair.Value as Dictionary<string, object>;
                    if (item == null)
                        continue;
                    GameProfile profile = new GameProfile { Mixed = string.Equals(Text(item, "mixed"), "true", StringComparison.OrdinalIgnoreCase) };
                    profile.Tags.UnionWith(Strings(Get(item, "tags")));
                    profile.Types.UnionWith(Strings(Get(item, "types")));
                    profile.Roles.UnionWith(Strings(Get(item, "roles")));
                    profile.Eras.UnionWith(Strings(Get(item, "eras")));
                    v.Profiles[pair.Key] = profile;
                }
            }

            // Aircraft names: the explicit nicknames plus what the catalog keys give ("F-16C_50" -> "f 16 c 50",
            // designation "f 16 c", family "f 16"). Variants (_easy) and input layers come in through their base.
            Dictionary<string, HashSet<string>> aliases = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            Dictionary<string, object> explicitAliases = Get(root, "aircraft_aliases") as Dictionary<string, object>;
            if (explicitAliases != null)
            {
                foreach (KeyValuePair<string, object> pair in explicitAliases)
                {
                    foreach (string key in Strings(pair.Value))
                    {
                        if (catalog != null && !catalog.Aircraft.ContainsKey(key))
                            v.Problems.Add("Aircraft alias '" + pair.Key + "' names unknown aircraft '" + key + "'.");
                        AddAlias(aliases, Phrase(pair.Key), key);
                        // Nicknames without digits ("viper", "hornet") tell the local model which aircraft a name means.
                        List<string> nicks;
                        if (!v.Nicknames.TryGetValue(key, out nicks))
                            v.Nicknames[key] = nicks = new List<string>();
                        if (!pair.Key.Any(char.IsDigit) && pair.Key.Length >= 3 && !nicks.Contains(pair.Key))
                            nicks.Add(pair.Key);
                    }
                }
            }
            if (catalog != null)
            {
                Regex family = new Regex(@"^([A-Za-z]+)-(\d+)", RegexOptions.CultureInvariant);
                foreach (KeyValuePair<string, EngineAutoGroup.Aircraft> plane in catalog.Aircraft)
                {
                    EngineAutoGroup.Aircraft info = plane.Value;
                    if (!EngineAutoGroup.IsDcs(info.Game) || info.VariantOf.Length > 0 || info.Type == "layer" || info.Type == "ground")
                        continue;
                    AddAlias(aliases, Phrase(plane.Key), plane.Key);
                    string designation = plane.Key.Split('_', ' ')[0];
                    if (designation.Length > 2)
                        AddAlias(aliases, Phrase(designation), plane.Key);
                    Match m = family.Match(plane.Key);
                    if (m.Success)
                        AddAlias(aliases, Phrase(m.Groups[1].Value + " " + m.Groups[2].Value), plane.Key);
                }
            }
            foreach (KeyValuePair<string, HashSet<string>> alias in aliases)
            {
                if (alias.Key.Length == 0)
                    continue;
                v.Aliases[alias.Key] = alias.Value.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                AddHit(v, alias.Key, new Hit { Kind = HitKind.Aircraft, Aircraft = v.Aliases[alias.Key], Phrase = alias.Key });
            }
            return v;
        }

        private static void AddAlias(Dictionary<string, HashSet<string>> aliases, string phrase, string key)
        {
            HashSet<string> keys;
            if (!aliases.TryGetValue(phrase, out keys))
                aliases[phrase] = keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            keys.Add(key);
        }

        // One meaning per phrase: functions first, then games, classes and aircraft names.
        private static void AddHit(Vocabulary v, string phrase, Hit hit)
        {
            if (phrase.Length == 0)
                return;
            Hit existing;
            if (v.Index.TryGetValue(phrase, out existing))
            {
                if (existing.Kind != hit.Kind || existing.Concept != hit.Concept || existing.Game != hit.Game || existing.Category != hit.Category)
                {
                    if (hit.Kind < existing.Kind)
                        v.Index[phrase] = hit;
                    if (existing.Kind != hit.Kind || existing.Kind != HitKind.Aircraft)
                        v.Problems.Add("Term '" + phrase + "' means " + Describe(existing) + " and " + Describe(hit) + "; using " + Describe(v.Index[phrase]) + ".");
                }
                return;
            }
            v.Index[phrase] = hit;
            v.MaxPhrase = Math.Max(v.MaxPhrase, phrase.Split(' ').Length);
        }

        private static string Describe(Hit hit)
        {
            switch (hit.Kind)
            {
                case HitKind.Function: return "function " + hit.Concept.Id;
                case HitKind.Game: return "game " + hit.Game.Id;
                case HitKind.Category: return "class " + hit.Category.Id;
                default: return "aircraft " + string.Join("|", hit.Aircraft);
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Parsing
        // ------------------------------------------------------------------------------------------------------

        internal sealed class Span
        {
            public int Start;
            public int Length;
            public Hit Hit;
            public int End { get { return Start + Length - 1; } }
        }

        internal sealed class DirectionSpan
        {
            public int Start;
            public int Length;
            public Span Concept;
            public List<Variant> Variants = new List<Variant>();
            public bool Excluded;
        }

        internal sealed class Clause
        {
            public List<GameWord> Games = new List<GameWord>();
            public List<CategoryWord> Categories = new List<CategoryWord>();
            public List<string> Aircraft = new List<string>();
            public List<string> AircraftWords = new List<string>();

            public string Label
            {
                get
                {
                    List<string> parts = new List<string>();
                    if (Games.Count > 0)
                        parts.Add(string.Join(" and ", Games.Select(g => g.Label)));
                    // Era and role words read before the kind ("WW2 fighters", "modern jets").
                    foreach (string dimension in new[] { "era", "class", "role", "kind" })
                    {
                        List<CategoryWord> cats = Categories.Where(c => c.Dimension == dimension).ToList();
                        if (cats.Count > 0)
                            parts.Add(string.Join(" and ", cats.Select(c => c.Label)));
                    }
                    foreach (CategoryWord other in Categories.Where(c => c.Dimension != "era" && c.Dimension != "class" && c.Dimension != "role" && c.Dimension != "kind"))
                        parts.Add(other.Label);
                    if (AircraftWords.Count > 0)
                        parts.Add(string.Join(", ", AircraftWords));
                    return string.Join(" ", parts);
                }
            }
        }

        internal sealed class Selection
        {
            public Concept Concept;
            public Variant Variant;
            public string Label = string.Empty;
            public List<string> Slots = new List<string>();
            public double Factor = 1.0;
            public string Why = string.Empty;
        }

        internal sealed class Understanding
        {
            public List<string> Tokens = new List<string>();
            public List<Span> Spans = new List<Span>();
            public List<DirectionSpan> Directions = new List<DirectionSpan>();
            public List<Selection> Selections = new List<Selection>();
            public List<Clause> Include = new List<Clause>();
            public List<Clause> Exclude = new List<Clause>();
            public List<string> NotUnderstood = new List<string>();
            public List<string> ContentWords = new List<string>();
            public int ExcludeFrom = int.MaxValue;
            public bool UsedRetrieval;
        }

        internal static Understanding Parse(string request, Vocabulary v, Catalog catalog)
        {
            Understanding u = new Understanding();
            u.Tokens = Tokenize(request);
            List<string> t = u.Tokens;
            bool[] used = new bool[t.Count];

            // Exclusion words ("except", "but not", "without") start the part that removes aircraft.
            for (int i = 0; i < t.Count; i++)
            {
                foreach (string exclusion in v.Exclusions.OrderByDescending(e => e.Split(' ').Length))
                {
                    string[] words = exclusion.Split(' ');
                    if (i + words.Length <= t.Count && Enumerable.Range(0, words.Length).All(k => t[i + k] == words[k]))
                    {
                        u.ExcludeFrom = Math.Min(u.ExcludeFrom, i);
                        for (int k = 0; k < words.Length; k++)
                            used[i + k] = true;
                    }
                }
            }

            // Pass A: functions, games, classes and aircraft names, longest phrase first.
            for (int i = 0; i < t.Count;)
            {
                bool matched = false;
                for (int length = Math.Min(v.MaxPhrase, t.Count - i); length >= 1; length--)
                {
                    if (Enumerable.Range(i, length).Any(k => used[k]))
                        continue;
                    Hit hit;
                    if (v.Index.TryGetValue(string.Join(" ", t.Skip(i).Take(length)), out hit))
                    {
                        u.Spans.Add(new Span { Start = i, Length = length, Hit = hit });
                        for (int k = i; k < i + length; k++)
                            used[k] = true;
                        i += length;
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                    i++;
            }

            // A longer direction of one function beats a shorter function word inside it ("throttle engine 1").
            List<Span> functions = u.Spans.Where(s => s.Hit.Kind == HitKind.Function).ToList();
            foreach (Span inner in functions.ToList())
            {
                foreach (Span outer in functions)
                {
                    if (outer == inner || outer.Hit.Concept == inner.Hit.Concept)
                        continue;
                    bool covered = outer.Hit.Concept.Variants.SelectMany(va => va.Terms).Any(term =>
                    {
                        string[] words = term.Split(' ');
                        if (words.Length <= inner.Length)
                            return false;
                        for (int start = inner.End - words.Length + 1; start <= inner.Start; start++)
                        {
                            if (start < 0 || start + words.Length > t.Count)
                                continue;
                            if (Enumerable.Range(0, words.Length).All(k => t[start + k] == words[k]))
                                return true;
                        }
                        return false;
                    });
                    if (covered)
                    {
                        u.Spans.Remove(inner);
                        for (int k = inner.Start; k <= inner.End; k++)
                            used[k] = false;
                        functions.Remove(inner);
                        break;
                    }
                }
            }

            // Pass B: directions of the functions found ("in and out", "up/down", "nose down", "left").
            List<Span> concepts = u.Spans.Where(s => s.Hit.Kind == HitKind.Function).OrderBy(s => s.Start).ToList();
            List<DirectionSpan> candidates = new List<DirectionSpan>();
            for (int i = 0; i < t.Count; i++)
            {
                for (int length = Math.Min(4, t.Count - i); length >= 1; length--)
                {
                    if (Enumerable.Range(i, length).Any(k => used[k]))
                        continue;
                    if (length == 1 && IsPreposition(v, u, t, i))
                        continue;
                    string key = string.Join(" ", t.Skip(i).Take(length));
                    foreach (Span concept in concepts)
                    {
                        List<Variant> variants = concept.Hit.Concept.Variants.Where(va => va.Terms.Contains(key)).ToList();
                        if (variants.Count > 0)
                            candidates.Add(new DirectionSpan { Start = i, Length = length, Concept = concept, Variants = variants });
                    }
                }
            }
            Dictionary<Span, int> assigned = concepts.ToDictionary(c => c, c => 0);
            foreach (IGrouping<string, DirectionSpan> group in candidates
                         .GroupBy(c => c.Start.ToString(CultureInfo.InvariantCulture) + ":" + c.Length.ToString(CultureInfo.InvariantCulture))
                         .OrderByDescending(g => g.First().Length).ThenBy(g => g.First().Start))
            {
                DirectionSpan first = group.First();
                if (Enumerable.Range(first.Start, first.Length).Any(k => used[k]))
                    continue;
                DirectionSpan best = group
                    .OrderBy(c => Distance(c.Concept, c.Start, c.Length))
                    .ThenBy(c => assigned[c.Concept])
                    .ThenBy(c => c.Concept.Start < c.Start ? 0 : 1)
                    .First();
                best.Excluded = best.Start > u.ExcludeFrom && best.Concept.Start < u.ExcludeFrom;
                assigned[best.Concept]++;
                u.Directions.Add(best);
                for (int k = best.Start; k < best.Start + best.Length; k++)
                    used[k] = true;
            }

            // Function selections: the named directions, else the function's default variants.
            // Directions count for the function, not for one of its words: "push to talk radio 1" = Comm 1 only.
            foreach (Span concept in concepts)
            {
                List<DirectionSpan> named = u.Directions.Where(d => d.Concept.Hit.Concept == concept.Hit.Concept && !d.Excluded).ToList();
                HashSet<Variant> excluded = new HashSet<Variant>(u.Directions.Where(d => d.Concept.Hit.Concept == concept.Hit.Concept && d.Excluded).SelectMany(d => d.Variants));
                IEnumerable<Variant> chosen = named.Count > 0
                    ? named.SelectMany(d => d.Variants)
                    : concept.Hit.Concept.Variants.Where(va => va.Default || concept.Hit.Concept.Variants.Count == 1);
                foreach (Variant variant in concept.Hit.Concept.Variants.Where(va => chosen.Contains(va) && !excluded.Contains(va)))
                {
                    if (u.Selections.Any(s => s.Slots.SequenceEqual(variant.Slots)))
                        continue;
                    u.Selections.Add(new Selection
                    {
                        Concept = concept.Hit.Concept,
                        Variant = variant,
                        Label = variant.Label,
                        Slots = variant.Slots.Where(s => catalog.Slots.ContainsKey(s)).ToList(),
                        Factor = 1.0,
                        Why = "'" + string.Join(" ", t.Skip(concept.Start).Take(concept.Length)) + "'" +
                              (named.Count > 0 ? " + '" + string.Join("', '", named.Where(d => d.Variants.Contains(variant)).Select(d => string.Join(" ", t.Skip(d.Start).Take(d.Length)))) + "'" : " (default directions)")
                    });
                }
            }

            BuildClauses(v, u, t);

            for (int i = 0; i < t.Count; i++)
            {
                if (used[i] || v.Stopwords.Contains(t[i]) || v.Quantifiers.Contains(t[i]) || v.Prepositions.Contains(t[i]) || t[i] == "and" || t[i] == "or")
                    continue;
                u.ContentWords.Add(t[i]);
            }

            // Nothing named a known function: look the remaining words up in the catalog's function names and the
            // descriptions of the controls (lower confidence).
            if (u.Selections.Count == 0 && u.ContentWords.Count > 0)
            {
                List<KeyValuePair<string, double>> ranked = RankSlots(u.ContentWords, v, catalog);
                if (ranked.Count > 0 && ranked[0].Value >= 1.0)
                {
                    double best = ranked[0].Value;
                    foreach (KeyValuePair<string, double> slot in ranked.Where(r => r.Value >= best * 0.8).Take(3))
                    {
                        EngineAutoGroup.Slot info = catalog.Slots[slot.Key];
                        u.Selections.Add(new Selection
                        {
                            Label = SlotLabel(v, info),
                            Slots = new List<string> { slot.Key },
                            Factor = RetrievalFactor,
                            Why = "words '" + string.Join("', '", u.ContentWords.Where(w => SlotDocument(v, catalog, slot.Key).Contains(w))) + "' found in the catalog description"
                        });
                    }
                    u.UsedRetrieval = true;
                    HashSet<string> explained = new HashSet<string>(u.Selections.SelectMany(s => SlotDocument(v, catalog, s.Slots[0])), StringComparer.Ordinal);
                    u.NotUnderstood.AddRange(u.ContentWords.Where(w => !explained.Contains(w)).Distinct());
                }
                else
                {
                    u.NotUnderstood.AddRange(u.ContentWords.Distinct());
                }
            }
            else
            {
                u.NotUnderstood.AddRange(u.ContentWords.Distinct());
            }
            return u;
        }

        // "in"/"on"/"for" before "all", "every", "the" or a game/aircraft word is a preposition, not a direction.
        private static bool IsPreposition(Vocabulary v, Understanding u, List<string> t, int i)
        {
            if (!v.Prepositions.Contains(t[i]))
                return false;
            if (i + 1 >= t.Count)
                return false;
            if (v.Quantifiers.Contains(t[i + 1]))
                return true;
            return u.Spans.Any(s => s.Start == i + 1 && s.Hit.Kind != HitKind.Function);
        }

        private static int Distance(Span concept, int start, int length)
        {
            if (start > concept.End)
                return start - concept.End - 1;
            return concept.Start - (start + length - 1) - 1;
        }

        private static void BuildClauses(Vocabulary v, Understanding u, List<string> t)
        {
            foreach (bool exclude in new[] { false, true })
            {
                List<Clause> clauses = exclude ? u.Exclude : u.Include;
                List<Span> scope = u.Spans.Where(s => s.Hit.Kind != HitKind.Function && (s.Start > u.ExcludeFrom) == exclude).OrderBy(s => s.Start).ToList();
                Clause current = null;
                Clause named = null;
                Span previous = null;
                int lastGameClause = -1;
                foreach (Span span in scope)
                {
                    string between = previous == null ? string.Empty : " " + string.Join(" ", t.Skip(previous.End + 1).Take(span.Start - previous.End - 1)) + " ";
                    bool conjunction = between.Contains(" and ") || between.Contains(" or ") || between.Contains(" plus ") || between.Contains(" as well as ");
                    bool preposition = v.Prepositions.Any(p => between.Contains(" " + p + " "));
                    string words = string.Join(" ", t.Skip(span.Start).Take(span.Length));
                    switch (span.Hit.Kind)
                    {
                        case HitKind.Game:
                            if (current != null && previous != null && previous.Hit.Kind == HitKind.Game && current.Categories.Count == 0 && current.Games.Count > 0)
                            {
                                current.Games.Add(span.Hit.Game);
                            }
                            else if (current != null && current.Games.Count == 0 && current.Categories.Count > 0 && (preposition || !conjunction))
                            {
                                // "jets and helicopters in DCS": the game names every class clause since the last game.
                                for (int k = lastGameClause + 1; k < clauses.Count; k++)
                                {
                                    if (clauses[k].Games.Count == 0 && clauses[k].Categories.Count > 0)
                                        clauses[k].Games.Add(span.Hit.Game);
                                }
                            }
                            else
                            {
                                current = new Clause();
                                current.Games.Add(span.Hit.Game);
                                clauses.Add(current);
                            }
                            lastGameClause = clauses.Count - 1;
                            break;
                        case HitKind.Category:
                            if (current == null)
                            {
                                current = new Clause();
                                clauses.Add(current);
                            }
                            else if (conjunction && current.Categories.Count > 0 && !current.Categories.Any(c => c.Dimension == span.Hit.Category.Dimension))
                            {
                                // "jets and WW2 fighters": a new class after "and" starts a new group in the same games.
                                Clause next = new Clause();
                                next.Games.AddRange(current.Games);
                                current = next;
                                clauses.Add(current);
                            }
                            current.Categories.Add(span.Hit.Category);
                            break;
                        default:
                            if (named == null)
                            {
                                named = new Clause();
                                clauses.Add(named);
                            }
                            foreach (string key in span.Hit.Aircraft)
                            {
                                if (!named.Aircraft.Contains(key, StringComparer.OrdinalIgnoreCase))
                                    named.Aircraft.Add(key);
                            }
                            string aircraftWords = span.Hit.Aircraft.Count <= 3 ? string.Join(", ", span.Hit.Aircraft) : Capitalize(words);
                            if (!named.AircraftWords.Contains(aircraftWords))
                                named.AircraftWords.Add(aircraftWords);
                            break;
                    }
                    previous = span;
                }
            }
        }

        private static string Capitalize(string words)
        {
            return string.Join(" ", words.Split(' ').Select(w => w.Length > 3 ? char.ToUpperInvariant(w[0]) + w.Substring(1) : w.ToUpperInvariant()));
        }

        // ------------------------------------------------------------------------------------------------------
        // Catalog retrieval (fallback and LLM candidates)
        // ------------------------------------------------------------------------------------------------------

        private static readonly Dictionary<string, HashSet<string>> DocumentCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        internal static HashSet<string> SlotDocument(Vocabulary v, Catalog catalog, string slotId)
        {
            HashSet<string> doc;
            if (DocumentCache.TryGetValue(slotId, out doc))
                return doc;
            doc = new HashSet<string>(StringComparer.Ordinal);
            EngineAutoGroup.Slot slot;
            if (catalog.Slots.TryGetValue(slotId, out slot))
            {
                doc.UnionWith(Tokenize(SplitCamel(slot.Name)));
                doc.UnionWith(Tokenize(slot.Id.Replace('_', ' ')));
            }
            List<RowInfo> rows;
            if (catalog.RowsBySlot.TryGetValue(slotId, out rows))
            {
                foreach (string control in rows.Select(r => r.Control).Distinct())
                    doc.UnionWith(Tokenize(control));
            }
            foreach (Concept concept in v.Concepts)
            {
                foreach (Variant variant in concept.Variants.Where(va => va.Slots.Contains(slotId)))
                {
                    foreach (string term in concept.Terms.Concat(variant.Terms))
                        doc.UnionWith(term.Split(' '));
                    doc.UnionWith(Tokenize(variant.Label));
                    doc.UnionWith(Tokenize(concept.System));
                    doc.UnionWith(Tokenize(concept.About));
                }
            }
            doc.ExceptWith(v.Stopwords);
            DocumentCache[slotId] = doc;
            return doc;
        }

        private static readonly Dictionary<string, HashSet<string>> TermWordCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // The words of the function terms and direction labels a slot belongs to (language.json).
        private static HashSet<string> TermWords(Vocabulary v, string slotId)
        {
            HashSet<string> words;
            if (TermWordCache.TryGetValue(slotId, out words))
                return words;
            words = new HashSet<string>(StringComparer.Ordinal);
            foreach (Concept concept in v.Concepts)
            {
                foreach (Variant variant in concept.Variants.Where(va => va.Slots.Contains(slotId)))
                {
                    foreach (string term in concept.Terms)
                        words.UnionWith(term.Split(' '));
                    words.UnionWith(Tokenize(variant.Label));
                }
            }
            words.ExceptWith(v.Stopwords);
            TermWordCache[slotId] = words;
            return words;
        }

        private static string SplitCamel(string name)
        {
            return Regex.Replace(name ?? string.Empty, "([a-z])([A-Z])", "$1 $2");
        }

        // Words that are rare across the catalog weigh more (inverse document frequency).
        internal static List<KeyValuePair<string, double>> RankSlots(IEnumerable<string> words, Vocabulary v, Catalog catalog)
        {
            List<string> query = words.Where(w => !v.Stopwords.Contains(w)).Distinct().ToList();
            List<KeyValuePair<string, double>> ranked = new List<KeyValuePair<string, double>>();
            if (query.Count == 0)
                return ranked;
            int total = catalog.Slots.Count;
            Dictionary<string, int> frequency = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string slot in catalog.Slots.Keys)
            {
                HashSet<string> doc = SlotDocument(v, catalog, slot);
                foreach (string word in query)
                {
                    if (doc.Contains(word))
                        frequency[word] = (frequency.ContainsKey(word) ? frequency[word] : 0) + 1;
                }
            }
            foreach (string slot in catalog.Slots.Keys)
            {
                if (!catalog.RowsBySlot.ContainsKey(slot))
                    continue;
                HashSet<string> doc = SlotDocument(v, catalog, slot);
                HashSet<string> named = TermWords(v, slot);
                double score = 0;
                foreach (string word in query)
                {
                    // A word people use for the function ("bomb" in "bomb release") counts twice: it names the
                    // function, a word in a control description only mentions it.
                    if (doc.Contains(word))
                        score += Math.Log(1.0 + total / (double)frequency[word]) * (named.Contains(word) ? 2.0 : 1.0);
                }
                if (score > 0)
                    ranked.Add(new KeyValuePair<string, double>(slot, score));
            }
            return ranked.OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
        }

        internal static string SlotLabel(Vocabulary v, EngineAutoGroup.Slot slot)
        {
            foreach (Concept concept in v.Concepts)
            {
                Variant variant = concept.Variants.FirstOrDefault(va => va.Slots.Count == 1 && va.Slots[0] == slot.Id);
                if (variant != null)
                    return variant.Label;
            }
            // A slot named like an id ("SPACE_STRAFE_VER", "SPACE_QUANTUM_ENGAGE"): its words, readable.
            if (Regex.IsMatch(slot.Name ?? string.Empty, @"^[A-Z0-9]+(?:[_-][A-Za-z0-9]+)+$"))
            {
                List<string> words = Regex.Split(slot.Name, @"[_-]+").Where(w => w.Length > 0).Select(w => w.ToLowerInvariant()).ToList();
                if (words.Count > 1 && words[0] == "space")
                    words.RemoveAt(0);
                Dictionary<string, string> spelled = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "ver", "up / down" }, { "hor", "left / right" }, { "fwdaft", "forward / back" }, { "esp", "ESP" }, { "ifcs", "IFCS" }, { "vtol", "VTOL" }, { "g", "G" }
                };
                string text = string.Join(" ", words.Select(w => spelled.ContainsKey(w) ? spelled[w] : w));
                return char.ToUpperInvariant(text[0]) + text.Substring(1);
            }
            return SplitCamel(slot.Name).Replace("__", " ").Replace('_', ' ').Trim();
        }

        // ------------------------------------------------------------------------------------------------------
        // Aircraft resolution
        // ------------------------------------------------------------------------------------------------------

        internal sealed class ScopeHit
        {
            public string Plane = string.Empty;
            public double Quality = 1.0;
            public string Reason = string.Empty;
        }

        internal static string GameOf(EngineAutoGroup.Aircraft info)
        {
            return EngineAutoGroup.IsDcs(info.Game) ? "DCS" : info.Game;
        }

        private static string GameLabel(Vocabulary v, string game)
        {
            GameWord word = v.Games.FirstOrDefault(g => string.Equals(g.Id, game, StringComparison.OrdinalIgnoreCase));
            return word == null ? game : word.Label;
        }

        // Full match, partial match (a game whose binds are shared by all its aircraft and that also flies other
        // kinds) or none.
        private static double CategoryQuality(Vocabulary v, string plane, EngineAutoGroup.Aircraft info, CategoryWord category)
        {
            if (info.Type == "layer" || info.Type == "ground")
                return 0;
            string baseName = info.VariantOf.Length > 0 ? info.VariantOf : plane;
            GameProfile profile;
            if (!EngineAutoGroup.IsDcs(info.Game) && v.Profiles.TryGetValue(plane, out profile))
            {
                bool hit = category.Tags.Overlaps(profile.Tags) || category.Types.Overlaps(profile.Types) ||
                           category.Roles.Overlaps(profile.Roles) || category.Eras.Overlaps(profile.Eras);
                if (!hit)
                    return 0;
                return profile.Mixed ? PartialGameFactor : 1.0;
            }
            HashSet<string> tags;
            if (!v.Tags.TryGetValue(baseName, out tags))
                tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool match = category.Tags.Overlaps(tags) || category.Types.Contains(info.Type) ||
                         category.Roles.Contains(LookupRole(plane)) || category.Eras.Contains(info.Era);
            return match ? 1.0 : 0;
        }

        // aircraft.json carries the role; EngineAutoGroup.Aircraft does not, so it is read once here.
        private static Dictionary<string, string> roles;

        internal static string LookupRole(string plane)
        {
            if (roles == null)
            {
                roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string file = Path.Combine(FunctionsFolder(), "aircraft.json");
                    foreach (KeyValuePair<string, Dictionary<string, object>> item in EngineSerialization.FromJson<Dictionary<string, Dictionary<string, object>>>(File.ReadAllText(file, Encoding.UTF8)))
                        roles[item.Key] = Text(item.Value, "role");
                }
                catch
                {
                }
            }
            string role;
            return roles.TryGetValue(plane, out role) ? role : string.Empty;
        }

        internal static Dictionary<string, ScopeHit> ResolveClauses(Vocabulary v, Catalog catalog, List<Clause> clauses, bool everythingWhenEmpty)
        {
            Dictionary<string, ScopeHit> result = new Dictionary<string, ScopeHit>(StringComparer.OrdinalIgnoreCase);
            if (clauses.Count == 0)
            {
                if (everythingWhenEmpty)
                {
                    foreach (string plane in catalog.Aircraft.Keys)
                        result[plane] = new ScopeHit { Plane = plane, Quality = 1.0, Reason = "all aircraft and games" };
                }
                return result;
            }
            foreach (Clause clause in clauses)
            {
                foreach (KeyValuePair<string, EngineAutoGroup.Aircraft> plane in catalog.Aircraft)
                {
                    EngineAutoGroup.Aircraft info = plane.Value;
                    string game = GameOf(info);
                    double quality;
                    string reason;
                    if (clause.Aircraft.Count > 0)
                    {
                        bool named = clause.Aircraft.Contains(plane.Key, StringComparer.OrdinalIgnoreCase) ||
                                     (info.VariantOf.Length > 0 && clause.Aircraft.Contains(info.VariantOf, StringComparer.OrdinalIgnoreCase));
                        if (!named)
                            continue;
                        quality = 1.0;
                        reason = "named in the request (" + string.Join(", ", clause.AircraftWords) + ")";
                    }
                    else
                    {
                        if (clause.Games.Count > 0 && !clause.Games.Any(g => g.Covers(game)))
                            continue;
                        quality = 1.0;
                        bool partial = false;
                        foreach (IGrouping<string, CategoryWord> dimension in clause.Categories.GroupBy(c => c.Dimension))
                        {
                            double best = dimension.Max(c => CategoryQuality(v, plane.Key, info, c));
                            if (best <= 0)
                            {
                                quality = 0;
                                break;
                            }
                            if (best < 1.0)
                                partial = true;
                            quality = Math.Min(quality, best);
                        }
                        if (quality <= 0)
                            continue;
                        string label = clause.Label;
                        reason = partial
                            ? GameLabel(v, game) + " binds are shared by all its aircraft; it also has " + string.Join(", ", clause.Categories.Select(c => c.Label))
                            : (label.Length > 0 ? label : "all aircraft and games");
                    }
                    ScopeHit existing;
                    if (!result.TryGetValue(plane.Key, out existing) || existing.Quality < quality)
                        result[plane.Key] = new ScopeHit { Plane = plane.Key, Quality = quality, Reason = reason };
                }
            }
            return result;
        }

        internal static string ScopeLabel(Understanding u)
        {
            string include = string.Join(", ", u.Include.Select(c => c.Label).Where(l => l.Length > 0));
            string exclude = string.Join(", ", u.Exclude.Select(c => c.Label).Where(l => l.Length > 0));
            if (exclude.Length > 0)
                return (include.Length > 0 ? include : "all") + " without " + exclude;
            return include;
        }

        // ------------------------------------------------------------------------------------------------------
        // Run
        // ------------------------------------------------------------------------------------------------------

        public static EngineNlGroupResult Run(Options options)
        {
            EngineNlGroupResult result = new EngineNlGroupResult { Request = options.Request ?? string.Empty, DryRun = options.DryRun };
            Vocabulary v;
            Catalog catalog;
            if (!TryLoad(result.Errors, out v, out catalog))
                return result;
            if (string.IsNullOrWhiteSpace(options.Request))
            {
                result.Errors.Add("Describe what to group, e.g. \"speed brake in and out on all jets\".");
                return result;
            }

            Understanding u = Parse(options.Request, v, catalog);
            result.Method = u.UsedRetrieval ? "offline (catalog words)" : "offline rules";
            result.NotUnderstood.AddRange(u.NotUnderstood);
            List<Selection> selections = u.Selections;
            Dictionary<string, ScopeHit> scope = ResolveClauses(v, catalog, u.Include, true);
            Dictionary<string, ScopeHit> excluded = ResolveClauses(v, catalog, u.Exclude, false);
            string scopeLabel = ScopeLabel(u);

            if (options.ShowPrompt)
                ShowPrompt(options, v, catalog, u, result);

            // The local model, when the user enabled it: it picks from the candidates the offline matcher found.
            string mode = (options.LlmMode ?? string.Empty).Trim().ToLowerInvariant();
            EngineLlm.Settings settings = EngineLlm.LoadSettings();
            if (mode.Length == 0)
                mode = settings.Enabled ? "auto" : "off";
            result.Llm.Mode = mode;
            bool askModel = mode == "on" || (mode == "auto" && (selections.Count == 0 || u.UsedRetrieval || u.NotUnderstood.Count > 0));
            if (askModel)
            {
                LlmChoice choice = AskModel(options.Request, v, catalog, u, result);
                if (choice != null)
                {
                    selections = choice.Selections;
                    scope = choice.Scope;
                    excluded = choice.Excluded;
                    scopeLabel = choice.ScopeLabel;
                    result.Method = "local model (" + result.Llm.Model + ") on offline candidates";
                    result.NotUnderstood.Clear();
                }
            }

            foreach (string key in excluded.Keys)
                scope.Remove(key);
            ApplyFilters(options, catalog, scope, result);

            result.Understood.Functions = selections.Select(s => s.Label + " [" + string.Join(", ", s.Slots) + "] from " + s.Why).ToList();
            result.Understood.Aircraft = scopeLabel.Length > 0 ? scopeLabel : "all aircraft and games";
            result.Understood.AircraftCount = scope.Count;

            // The function map understands every function by its names, synonyms, slang and everyday words ("fast
            // travel", "drop the wheels", "Nachbrenner"); where the word rules found nothing, found it only by the
            // catalog's words or found another function than the map names, the map's reading makes the proposals.
            if (!result.Llm.Used && scope.Count > 0 && ProposeFromFunctionMap(options, u, selections, scope, scopeLabel, result))
            {
                Apply(options, result);
                return result;
            }

            if (selections.Count == 0)
            {
                result.Errors.Add("No function recognised in \"" + options.Request + "\". Name it like the cockpit does, e.g. speed brake, gear, flaps, trim hat, CMS, TMS, master arm, NWS" +
                                  (result.NotUnderstood.Count > 0 ? " (not understood: " + string.Join(", ", result.NotUnderstood) + ")." : "."));
                return result;
            }
            if (scope.Count == 0)
            {
                result.Errors.Add("No aircraft or game matches \"" + (scopeLabel.Length > 0 ? scopeLabel : options.Request) + "\".");
                return result;
            }

            BuildProposals(options, v, catalog, selections, scope, scopeLabel, result);
            Apply(options, result);
            return result;
        }

        private static bool ProposeFromFunctionMap(Options options, Understanding u, List<Selection> selections, Dictionary<string, ScopeHit> scope, string scopeLabel, EngineNlGroupResult result)
        {
            EngineFunctionMap.Map map;
            try
            {
                map = EngineFunctionMap.Load(new List<string>());
            }
            catch (Exception)
            {
                return false;
            }
            // The words that are no aircraft, class or game ("flaps" of "flaps in X-Plane 12").
            HashSet<int> scopeWords = new HashSet<int>(u.Spans.Where(s => s.Hit.Kind != HitKind.Function).SelectMany(s => Enumerable.Range(s.Start, s.Length)));
            int excludeFrom = Math.Min(u.ExcludeFrom, u.Tokens.Count);
            string words = string.Join(" ", u.Tokens.Where((w, i) => !scopeWords.Contains(i) && i < excludeFrom));
            if (words.Trim().Length == 0)
                return false;
            EngineFunctionMap.Understanding understood = EngineFunctionMap.Resolve(map, words);
            List<EngineFunctionMap.Resolved> named = understood.Functions.Where(r => !r.Related).ToList();
            if (named.Count == 0)
                return false;
            bool rulesWeak = selections.Count == 0 || u.UsedRetrieval;
            // The rules found a function: they stand unless the map names another one with the user's words.
            HashSet<string> ruleSlots = new HashSet<string>(selections.SelectMany(s => s.Slots), StringComparer.OrdinalIgnoreCase);
            EngineFunctionMap.Resolved top = named[0];
            bool sameAsRules = named.Any(r => r.Function.Slots.Any(l => ruleSlots.Contains(l.Slot)));
            if (!rulesWeak && (sameAsRules || top.Described && top.Covered < 2))
                return false;
            // Named functions all ("gear and flaps"); described ones: the best only.
            List<EngineFunctionMap.Resolved> chosen = named.Where(r => !r.Described).ToList();
            if (chosen.Count == 0)
                chosen.Add(top);
            HashSet<string> planes = new HashSet<string>(scope.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (EngineFunctionMap.Resolved r in chosen)
            {
                List<EngineFunctionMap.Entry> entries;
                if (!map.ByFunction.TryGetValue(r.Function.Id, out entries))
                    continue;
                List<KeyValuePair<EngineFunctionMap.Entry, EngineFunctionMap.Assignment>> fitting = new List<KeyValuePair<EngineFunctionMap.Entry, EngineFunctionMap.Assignment>>();
                foreach (EngineFunctionMap.Entry e in entries)
                {
                    string plane = e.Command.Game == "DCS" ? e.Command.Plane : e.Command.Game;
                    if (!planes.Contains(plane))
                        continue;
                    EngineFunctionMap.Assignment a = e.Of(r.Function.Id);
                    if (a == null || a.Weight < 0.6 || EngineIdMeanings.CatalogAgainstTitle(e, a))
                        continue;
                    if (r.Dir.Length > 0 && !e.Command.IsAxis && a.Dir.Length > 0 && !string.Equals(a.Dir, r.Dir, StringComparison.OrdinalIgnoreCase))
                        continue;
                    fitting.Add(new KeyValuePair<EngineFunctionMap.Entry, EngineFunctionMap.Assignment>(e, a));
                }
                if (fitting.Count == 0)
                    continue;
                bool isAxis = r.Function.Only == "axis" || (r.Function.Only != "button" && (r.Function.Kind == "axis" || (r.Function.Kind == "both" && fitting.Count(f => f.Key.Command.IsAxis) * 2 > fitting.Count)));
                string label = EngineFunctionMap.Describe(r.Function, r.Dir, isAxis);
                EngineNlGroupProposal proposal = new EngineNlGroupProposal
                {
                    Name = (string.IsNullOrWhiteSpace(options.Name) ? char.ToUpperInvariant(label[0]) + label.Substring(1) : options.Name.Trim()) + (scopeLabel.Length > 0 && string.IsNullOrWhiteSpace(options.Name) ? " (" + scopeLabel + ")" : string.Empty),
                    Function = label,
                    IsAxis = isAxis,
                    Why = "function map ('" + r.Term + "')"
                };
                // Per aircraft or game its best action of the function and direction.
                foreach (IGrouping<string, KeyValuePair<EngineFunctionMap.Entry, EngineFunctionMap.Assignment>> plane in fitting.Where(f => f.Key.Command.IsAxis == isAxis)
                             .GroupBy(f => f.Key.Command.Game == "DCS" ? f.Key.Command.Plane : f.Key.Command.Game, StringComparer.OrdinalIgnoreCase))
                {
                    KeyValuePair<EngineFunctionMap.Entry, EngineFunctionMap.Assignment> best = plane
                        .OrderByDescending(f => f.Value.Weight + (r.Dir.Length > 0 && string.Equals(f.Value.Dir, r.Dir, StringComparison.OrdinalIgnoreCase) ? 0.5 : 0) + (f.Value.Source == "catalog" ? 0.2 : 0))
                        .ThenBy(f => EngineFunctionMap.PrimaryRank(f.Key, r.Function)).First();
                    EngineRelationSuggest.Command c = best.Key.Command;
                    string game = c.Game == "DCS" ? "DCS" : c.Game;
                    EngineNlGroupNode node = proposal.Nodes.FirstOrDefault(n => n.Game == game && n.Id == c.Id);
                    double confidence = Math.Round(0.7 * best.Value.Weight * scope[plane.Key].Quality, 2);
                    if (node == null)
                    {
                        node = new EngineNlGroupNode { Game = game, Id = c.Id, Confidence = confidence };
                        proposal.Nodes.Add(node);
                    }
                    if (!node.Planes.Contains(plane.Key, StringComparer.OrdinalIgnoreCase))
                        node.Planes.Add(game == "DCS" ? plane.Key : game);
                    node.Confidence = Math.Min(node.Confidence, confidence);
                    string category = EngineAiExplain.ReadableCategory(c.Category);
                    if (node.Explanations.Count < 8)
                        node.Explanations.Add(PlaneLabel(c.Game, plane.Key) + ": \"" + EngineActionInfo.Readable(c.Game, c.Id, c.Title) + "\"" + (category.Length > 0 ? " [" + category + "]" : string.Empty) +
                                              " (function map: " + r.Function.Name + "); " + scope[plane.Key].Reason);
                }
                foreach (EngineNlGroupNode node in proposal.Nodes)
                {
                    if (node.Game == "DCS")
                    {
                        List<string> excludedPlanes;
                        List<string> notes;
                        node.Planes = EngineIdMeanings.Guard("DCS", node.Id, node.Planes, proposal.Name, out excludedPlanes, out notes);
                        node.Excluded = excludedPlanes;
                        node.ExcludedReasons = notes;
                    }
                    node.Spec = node.Game + ":" + node.Id + ":" + string.Join("|", node.Planes);
                }
                proposal.Nodes.RemoveAll(n => n.Planes.Count == 0 && n.Excluded.Count == 0);
                if (proposal.Nodes.Count > 0)
                    result.Proposals.Add(proposal);
            }
            if (result.Proposals.Count == 0)
                return false;
            result.Method = "function map (offline)";
            result.NotUnderstood.Clear();
            result.Understood.Functions = chosen.Select(r => EngineFunctionMap.Describe(r.Function, r.Dir, false) + " from '" + r.Term + "' (function map)").ToList();
            return true;
        }

        private static void ApplyFilters(Options options, Catalog catalog, Dictionary<string, ScopeHit> scope, EngineNlGroupResult result)
        {
            HashSet<string> games = new HashSet<string>(options.Games ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> planes = new HashSet<string>(options.Planes ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (string key in scope.Keys.ToList())
            {
                EngineAutoGroup.Aircraft info = catalog.Aircraft[key];
                if (games.Count > 0 && !games.Contains(GameOf(info)))
                    scope.Remove(key);
                else if (planes.Count > 0 && !planes.Contains(key) && !planes.Contains(info.VariantOf) && !planes.Contains(GameOf(info) == "DCS" ? "\u0000" : GameOf(info)))
                    scope.Remove(key);
            }
        }

        private static void BuildProposals(Options options, Vocabulary v, Catalog catalog, List<Selection> selections, Dictionary<string, ScopeHit> scope, string scopeLabel, EngineNlGroupResult result)
        {
            Dictionary<string, bool?> axisCache = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> missingDb = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Selection selection in selections)
            {
                if (selection.Slots.Count == 0)
                    continue;
                EngineAutoGroup.Slot slot = catalog.Slots[selection.Slots[0]];
                string name = selection.Label + (scopeLabel.Length > 0 ? " (" + scopeLabel + ")" : string.Empty);
                if (!string.IsNullOrWhiteSpace(options.Name))
                    name = selections.Count == 1 ? options.Name.Trim() : options.Name.Trim() + " - " + selection.Label;
                EngineNlGroupProposal proposal = result.Proposals.FirstOrDefault(p => p.Name == name);
                if (proposal == null)
                {
                    proposal = new EngineNlGroupProposal
                    {
                        Name = name,
                        Function = selection.Label,
                        Slots = new List<string>(selection.Slots),
                        IsAxis = slot.Axis,
                        Why = selection.Why
                    };
                    result.Proposals.Add(proposal);
                }

                foreach (ScopeHit hit in scope.Values.OrderBy(h => h.Plane, StringComparer.OrdinalIgnoreCase))
                {
                    EngineAutoGroup.Aircraft info = catalog.Aircraft[hit.Plane];
                    List<RowInfo> found = new List<RowInfo>();
                    foreach (string slotId in selection.Slots)
                    {
                        List<RowInfo> rows;
                        if (!catalog.RowsBySlot.TryGetValue(slotId, out rows))
                            continue;
                        List<RowInfo> planeRows = rows.Where(r => string.Equals(r.Row.Plane, hit.Plane, StringComparison.OrdinalIgnoreCase)).ToList();
                        List<RowInfo> own = planeRows.Where(r => !string.Equals(r.Row.Status, "substitute", StringComparison.OrdinalIgnoreCase)).ToList();
                        found.AddRange(own.Count > 0 ? own : (options.Substitutes ? planeRows : new List<RowInfo>()));
                    }
                    if (found.Count == 0 && !EngineAutoGroup.IsDcs(info.Game) && AddFromFunctionMap(selection, proposal, hit))
                        continue;
                    if (found.Count == 0)
                    {
                        if (info.Type != "layer" && info.Type != "ground")
                            AddMissing(v, catalog, proposal, hit.Plane, selection);
                        continue;
                    }
                    foreach (RowInfo row in found)
                    {
                        bool? isAxis;
                        CommandInfo command = LookupCommand(row.Row.Game, row.Row.Plane, row.Row.Action);
                        if (EngineAutoGroup.IsDcs(row.Row.Game))
                        {
                            if (Commands(row.Row.Game, row.Row.Plane).Count == 0)
                            {
                                missingDb.Add(row.Row.Plane);
                                continue;
                            }
                            isAxis = command != null ? (bool?)(row.Row.Action.Length > 0 && char.ToLowerInvariant(row.Row.Action[0]) == 'a') : null;
                        }
                        else
                        {
                            string cacheKey = row.Row.Game + "|" + row.Row.Action;
                            if (!axisCache.TryGetValue(cacheKey, out isAxis))
                                axisCache[cacheKey] = isAxis = EngineAutoGroup.GameCommandAxis(row.Row.Game, row.Row.Action);
                        }
                        if (!isAxis.HasValue)
                        {
                            AddUnique(result.Warnings, row.Row.Plane + " " + row.Row.Action + ": not in the local database (refresh the database).");
                            continue;
                        }
                        if (isAxis.Value != proposal.IsAxis)
                            continue;
                        double confidence = Math.Round(StatusFactor(row.Row.Status) * hit.Quality * selection.Factor, 2);
                        string game = EngineAutoGroup.IsDcs(row.Row.Game) ? "DCS" : row.Row.Game;
                        EngineNlGroupNode node = proposal.Nodes.FirstOrDefault(n => n.Game == game && n.Id == row.Row.Action);
                        if (node == null)
                        {
                            node = new EngineNlGroupNode { Game = game, Id = row.Row.Action, Confidence = confidence };
                            proposal.Nodes.Add(node);
                        }
                        if (node.Planes.Contains(row.Row.Plane, StringComparer.OrdinalIgnoreCase))
                            continue;
                        node.Planes.Add(row.Row.Plane);
                        node.Confidence = Math.Min(node.Confidence, confidence);
                        node.Explanations.Add(PlaneLabel(row.Row.Game, row.Row.Plane) + ": " + (row.Control.Length > 0 ? row.Control : SlotLabel(v, catalog.Slots[row.Row.Slot])) +
                                              DescribeCommand(row.Row.Game, row.Row.Action, command) + " (catalog " + row.Row.Status + "); " + hit.Reason);
                    }
                }
                foreach (EngineNlGroupNode node in proposal.Nodes)
                {
                    if (node.Game == "DCS")
                    {
                        List<string> excluded;
                        List<string> notes;
                        node.Planes = EngineIdMeanings.Guard("DCS", node.Id, node.Planes, proposal.Name, out excluded, out notes);
                        node.Excluded = excluded;
                        node.ExcludedReasons = notes;
                        // One explanation per aircraft of the node: the left-out ones say why in ExcludedReasons.
                        node.Explanations.RemoveAll(x => excluded.Any(p => x.StartsWith(p + ": ", StringComparison.OrdinalIgnoreCase)));
                    }
                    node.Spec = node.Game + ":" + node.Id + ":" + string.Join("|", node.Planes);
                }
                proposal.Nodes.RemoveAll(n => n.Planes.Count == 0 && n.Excluded.Count == 0);
            }
            foreach (string plane in missingDb.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                AddUnique(result.Warnings, plane + " is not in the local DCS database (refresh the DCS database to include it).");
            result.Proposals.RemoveAll(p => p.Nodes.Count == 0 && p.Missing.Count == 0);
        }

        private static void AddMissing(Vocabulary v, Catalog catalog, EngineNlGroupProposal proposal, string plane, Selection selection)
        {
            string hint = string.Empty;
            if (selection.Concept != null)
            {
                List<string> others = new List<string>();
                // Other directions of the function, then its relatives ("flares": CMS aft/left/right on this aircraft).
                IEnumerable<Variant> alternatives = selection.Concept.Variants.Where(va => va != selection.Variant)
                    .Concat(selection.Concept.Related.Select(id => v.Concepts.FirstOrDefault(c => c.Id == id)).Where(c => c != null).SelectMany(c => c.Variants))
                    .Where(va => !va.Slots.SequenceEqual(selection.Slots));
                foreach (Variant other in alternatives)
                {
                    if (others.Contains(other.Label))
                        continue;
                    bool has = other.Slots.Any(s =>
                    {
                        List<RowInfo> rows;
                        return catalog.RowsBySlot.TryGetValue(s, out rows) && rows.Any(r => string.Equals(r.Row.Plane, plane, StringComparison.OrdinalIgnoreCase) &&
                                                                                            !string.Equals(r.Row.Status, "substitute", StringComparison.OrdinalIgnoreCase));
                    });
                    if (has)
                        others.Add(other.Label);
                }
                if (others.Count > 0)
                    hint = " (has " + string.Join(", ", others) + ")";
            }
            EngineAutoGroup.Aircraft aircraft;
            proposal.Missing.Add((catalog.Aircraft.TryGetValue(plane, out aircraft) ? PlaneLabel(aircraft.Game, plane) : plane) + hint);
        }

        private static double StatusFactor(string status)
        {
            switch ((status ?? string.Empty).ToLowerInvariant())
            {
                case "confirmed":
                case "corrected":
                    return 0.95;
                case "new":
                    return 0.85;
                case "substitute":
                    return 0.55;
                default:
                    return 0.7;
            }
        }

        private static void Apply(Options options, EngineNlGroupResult result)
        {
            Dictionary<string, Relation> relations = InternalDataManagement.AllRelations;
            HashSet<string> select = new HashSet<string>(options.Select ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string spec in options.SkipNodes ?? new List<string>())
            {
                string game, id;
                List<string> planes;
                if (!TrySplitSpec(spec, out game, out id, out planes))
                {
                    result.Warnings.Add("Ignored --skip-node '" + spec + "' (use game:id:plane1|plane2).");
                    continue;
                }
                foreach (string plane in planes)
                    skip.Add(game + "|" + id + "|" + plane);
            }

            Dictionary<string, List<string>> usedElsewhere = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<string>> disabled = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> pair in relations)
            {
                if (pair.Value == null || pair.Value.NODES == null)
                    continue;
                foreach (RelationItem node in pair.Value.NODES)
                {
                    if (node == null || node.AIRCRAFT == null)
                        continue;
                    foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT)
                        AddOwner(plane.Value ? usedElsewhere : disabled, NodeKey(node.Game, node.ID, plane.Key), pair.Key);
                }
            }

            foreach (EngineNlGroupProposal proposal in result.Proposals)
            {
                proposal.Selected = select.Count == 0 || select.Contains(proposal.Name);
                Relation relation;
                bool exists = relations.TryGetValue(proposal.Name, out relation) && relation != null;
                proposal.Existing = exists;
                if (exists && relation.ISAXIS != proposal.IsAxis)
                {
                    AddUnique(result.Warnings, "Relation '" + proposal.Name + "' exists as " + (relation.ISAXIS ? "axis" : "button") + "; left unchanged.");
                    proposal.Selected = false;
                }
                foreach (EngineNlGroupNode node in proposal.Nodes)
                {
                    foreach (string plane in node.Planes)
                    {
                        string key = NodeKey(node.Game, node.Id, plane);
                        List<string> owners;
                        if (usedElsewhere.TryGetValue(key, out owners))
                        {
                            if (!owners.Contains(proposal.Name, StringComparer.OrdinalIgnoreCase))
                            {
                                node.KeptIn[plane] = string.Join(", ", owners);
                                AddUnique(result.Kept, plane + " " + node.Id + " stays in '" + string.Join("', '", owners) + "' (wanted: '" + proposal.Name + "')");
                            }
                            else
                            {
                                node.KeptIn[plane] = proposal.Name;
                            }
                        }
                        else if (disabled.TryGetValue(key, out owners))
                        {
                            node.KeptIn[plane] = string.Join(", ", owners) + " (switched off)";
                            AddUnique(result.Kept, plane + " " + node.Id + " stays switched off in '" + string.Join("', '", owners) + "'");
                        }
                    }
                }
                if (!proposal.Selected)
                    continue;

                int added = 0;
                foreach (EngineNlGroupNode node in proposal.Nodes)
                {
                    if (node.Confidence < options.MinConfidence)
                    {
                        AddUnique(result.Warnings, node.Spec + " below the confidence threshold " + options.MinConfidence.ToString("0.##", CultureInfo.InvariantCulture) + "; not added.");
                        continue;
                    }
                    foreach (string plane in node.Planes)
                    {
                        if (node.KeptIn.ContainsKey(plane) || skip.Contains(node.Game + "|" + node.Id + "|" + plane))
                            continue;
                        if (!options.DryRun)
                        {
                            if (relation == null)
                            {
                                relation = new Relation();
                                relation.NAME = proposal.Name;
                                relation.ISAXIS = proposal.IsAxis;
                                relation.Groups = new List<string> { GroupTag };
                                relation.NODES = new List<RelationItem>();
                            }
                            if (!relation.AddNode(node.Id, node.Game, proposal.IsAxis, plane))
                                continue;
                        }
                        added++;
                    }
                    // Left out for its meaning: in the node switched off, so it shows and one tick brings it back.
                    if (!options.DryRun && relation != null)
                    {
                        foreach (string plane in node.Excluded.Where(p => !node.KeptIn.ContainsKey(p)))
                        {
                            if (!relation.AddNode(node.Id, node.Game, proposal.IsAxis, plane))
                                continue;
                            RelationItem off = relation.NODES.FirstOrDefault(n => n != null && string.Equals(n.ID, node.Id, StringComparison.OrdinalIgnoreCase) &&
                                                                                 string.Equals(EngineRelationSuggest.GameKey(n.Game ?? string.Empty), "DCS", StringComparison.OrdinalIgnoreCase));
                            if (off != null)
                                off.SetAircraftActivity(plane, false);
                        }
                    }
                }
                proposal.NodesAdded = added;
                if (added == 0)
                    continue;
                if (exists)
                    result.RelationsExtended++;
                else
                    result.RelationsCreated++;
                result.NodesAdded += added;
                if (!options.DryRun && !exists)
                    relations[proposal.Name] = relation;
            }
        }

        internal static bool TrySplitSpec(string spec, out string game, out string id, out List<string> planes)
        {
            game = id = string.Empty;
            planes = new List<string>();
            string value = (spec ?? string.Empty).Trim();
            int first = value.IndexOf(':');
            int last = value.LastIndexOf(':');
            if (first <= 0 || last <= first)
                return false;
            game = value.Substring(0, first);
            id = value.Substring(first + 1, last - first - 1);
            planes = value.Substring(last + 1).Split('|').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (EngineAutoGroup.IsDcs(game))
                game = "DCS";
            return id.Length > 0 && planes.Count > 0;
        }

        private static string NodeKey(string game, string id, string plane)
        {
            return (EngineAutoGroup.IsDcs(game) ? "DCS" : game) + "|" + id + "|" + plane;
        }

        private static void AddOwner(Dictionary<string, List<string>> owners, string key, string relation)
        {
            List<string> names;
            if (!owners.TryGetValue(key, out names))
                owners[key] = names = new List<string>();
            if (!names.Contains(relation, StringComparer.OrdinalIgnoreCase))
                names.Add(relation);
        }

        private static void AddUnique(List<string> list, string value)
        {
            if (!list.Contains(value))
                list.Add(value);
        }

        internal sealed class LlmChoice
        {
            public List<Selection> Selections = new List<Selection>();
            public Dictionary<string, ScopeHit> Scope;
            public Dictionary<string, ScopeHit> Excluded;
            public string ScopeLabel = string.Empty;
        }

        internal sealed class Candidate
        {
            public string Id = string.Empty;
            public string Label = string.Empty;
            public string Detail = string.Empty;
            // Functions: the heading (function, cockpit system, what it does) its directions are printed under.
            public string Group = string.Empty;
            public Selection Selection;
            public List<Clause> Clauses = new List<Clause>();
            public bool Everything;
            // Scopes: everything, request (as the rules read it), class, game or aircraft.
            public string Kind = string.Empty;
        }

        // How much the prompt may hold (per model in DB\Llm\models.json; shrunk until the prompt fits the context).
        internal sealed class PromptLimits
        {
            public int MaxFunctions = 20;
            public int MaxScopes = 90;
            public int ExamplesPerFunction = 2;
            public int FewShot = 3;
        }

        // ------------------------------------------------------------------------------------------------------
        // Command titles and categories from the game databases (DB\DCS\<plane>.html, IL2.actions, actions.tsv)
        // ------------------------------------------------------------------------------------------------------

        internal sealed class CommandInfo
        {
            public string Title = string.Empty;
            public string Category = string.Empty;
            public bool IsAxis;
        }

        private static readonly Dictionary<string, Dictionary<string, CommandInfo>> CommandCache = new Dictionary<string, Dictionary<string, CommandInfo>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex Il2Action = new Regex(@"^\s*([A-Za-z0-9_]+)\s*,.*//\s*([ab])\s*(.*)$", RegexOptions.CultureInvariant);

        internal static Dictionary<string, CommandInfo> Commands(string game, string plane)
        {
            bool dcs = EngineAutoGroup.IsDcs(game);
            string key = dcs ? "DCS|" + plane : game;
            Dictionary<string, CommandInfo> map;
            if (CommandCache.TryGetValue(key, out map))
                return map;
            map = new Dictionary<string, CommandInfo>(StringComparer.Ordinal);
            try
            {
                if (dcs)
                {
                    foreach (DCSInput input in InternalDataManagement.ParseDcsHtmlInputs(GameDataPaths.DbFile("DCS", plane + ".html"), plane))
                    {
                        if (input != null && !string.IsNullOrWhiteSpace(input.ID) && !map.ContainsKey(input.ID))
                            map[input.ID] = new CommandInfo { Title = (input.Title ?? string.Empty).Trim(), Category = (input.Cat ?? string.Empty).Trim(), IsAxis = input.IsAxis };
                    }
                }
                else if (string.Equals(game, "IL2Game", StringComparison.OrdinalIgnoreCase))
                {
                    string file = GameDataPaths.DbFile("IL2", "IL2.actions");
                    if (File.Exists(file))
                    {
                        foreach (string line in File.ReadAllLines(file))
                        {
                            Match match = Il2Action.Match(line);
                            if (match.Success && !map.ContainsKey(match.Groups[1].Value))
                                map[match.Groups[1].Value] = new CommandInfo { Title = EngineReadableNames.Il2Title(match.Groups[1].Value, match.Groups[3].Value), IsAxis = match.Groups[2].Value == "a" };
                        }
                    }
                }
                else if (string.Equals(game, "StarCitizen", StringComparison.OrdinalIgnoreCase))
                {
                    // Star Citizen: the game list (actionmap|action), also under the catalog's spelling (actionmap___action).
                    foreach (EngineRelationSuggest.Command command in EngineRelationSuggest.Index(new List<string>()).Where(c => c.Game == "StarCitizen"))
                    {
                        CommandInfo info = new CommandInfo { Title = EngineActionInfo.Readable("StarCitizen", command.Id, command.Title), Category = command.Category ?? string.Empty, IsAxis = command.IsAxis };
                        if (!map.ContainsKey(command.Id))
                            map[command.Id] = info;
                        string catalogId = command.Id.Replace("|", "___");
                        if (!map.ContainsKey(catalogId))
                            map[catalogId] = info;
                    }
                }
                else
                {
                    IGameIntegration integration;
                    if (GameRegistry.TryResolve(game, out integration))
                    {
                        foreach (GameAction action in GameCatalogStore.Load(integration, null).Actions)
                        {
                            if (!map.ContainsKey(action.Id))
                                map[action.Id] = new CommandInfo { Title = (action.Title ?? string.Empty).Trim(), Category = (action.Category ?? string.Empty).Trim(), IsAxis = action.IsAxis };
                        }
                    }
                }
            }
            catch
            {
            }
            CommandCache[key] = map;
            return map;
        }

        internal static CommandInfo LookupCommand(string game, string plane, string id)
        {
            CommandInfo info;
            Dictionary<string, CommandInfo> commands = Commands(game, plane);
            if (commands.TryGetValue(id ?? string.Empty, out info))
                return info;
            // Star Citizen: the catalog writes "actionmap___action", the game list "actionmap|action".
            return commands.TryGetValue(EngineRelationSuggest.NodeId(game, id), out info) ? info : null;
        }

        // A DCS aircraft by its id, a game by its name ("StarCitizen" -> "Star Citizen").
        private static string PlaneLabel(string game, string plane)
        {
            return EngineAutoGroup.IsDcs(game) ? plane : EngineGameWords.Label(plane);
        }

        // ' = "CMS Switch - FWD" [HOTAS]': the game's own name of the command and where the game files it.
        private static string DescribeCommand(string game, string id, CommandInfo command)
        {
            if (command == null || command.Title.Length == 0)
                return string.Empty;
            string readable = EngineActionInfo.Readable(EngineAutoGroup.IsDcs(game) ? "DCS" : game, id, command.Title);
            string category = EngineAiExplain.ReadableCategory(command.Category);
            category = category.Length > 60 ? category.Substring(0, 60).TrimEnd() + "..." : category;
            return " = \"" + readable + "\"" + (category.Length > 0 ? " [" + category + "]" : string.Empty);
        }

        // A game without a catalog row for the function (Project Wingman, ACE COMBAT, Battlefield, FSX, a slot the
        // game's table leaves out): the function map's action of the same function and direction there, by its own
        // title, with a lower confidence and the reason said.
        private static bool AddFromFunctionMap(Selection selection, EngineNlGroupProposal proposal, ScopeHit hit)
        {
            if (selection.Slots.Count == 0)
                return false;
            EngineFunctionMap.Map map;
            try
            {
                map = EngineFunctionMap.Load(new List<string>());
            }
            catch (Exception)
            {
                return false;
            }
            string game = hit.Plane;
            List<KeyValuePair<EngineFunctionMap.Entry, double>> picks = new List<KeyValuePair<EngineFunctionMap.Entry, double>>();
            string functionName = string.Empty;
            foreach (EngineFunctionMap.Function f in map.Functions)
            {
                foreach (EngineFunctionMap.SlotLink link in f.Slots.Where(l => selection.Slots.Contains(l.Slot, StringComparer.OrdinalIgnoreCase)))
                {
                    List<EngineFunctionMap.Entry> entries;
                    if (!map.ByFunction.TryGetValue(f.Id, out entries))
                        continue;
                    foreach (EngineFunctionMap.Entry e in entries)
                    {
                        if (!string.Equals(e.Command.Game, game, StringComparison.OrdinalIgnoreCase) || e.Command.IsAxis != proposal.IsAxis)
                            continue;
                        EngineFunctionMap.Assignment a = e.Of(f.Id);
                        if (a == null || EngineIdMeanings.CatalogAgainstTitle(e, a))
                            continue;
                        if (link.Dir.Length > 0 && !e.Command.IsAxis && !string.Equals(a.Dir, link.Dir, StringComparison.OrdinalIgnoreCase))
                            continue;
                        functionName = f.Name;
                        picks.Add(new KeyValuePair<EngineFunctionMap.Entry, double>(e, a.Weight * link.Weight + (a.Source == "catalog" ? 0.2 : 0) - EngineFunctionMap.PrimaryRank(e, f) * 0.01));
                    }
                }
            }
            if (picks.Count == 0)
                return false;
            double best = picks.Max(p => p.Value);
            bool added = false;
            foreach (EngineFunctionMap.Entry e in picks.Where(p => p.Value >= best - 0.05).OrderByDescending(p => p.Value).Select(p => p.Key).Distinct().Take(2))
            {
                double confidence = Math.Round(0.65 * hit.Quality * selection.Factor, 2);
                EngineNlGroupNode node = proposal.Nodes.FirstOrDefault(n => n.Game == e.Command.Game && n.Id == e.Command.Id);
                if (node == null)
                {
                    node = new EngineNlGroupNode { Game = e.Command.Game, Id = e.Command.Id, Confidence = confidence };
                    proposal.Nodes.Add(node);
                }
                if (node.Planes.Contains(game, StringComparer.OrdinalIgnoreCase))
                    continue;
                node.Planes.Add(game);
                node.Confidence = Math.Min(node.Confidence, confidence);
                string category = EngineAiExplain.ReadableCategory(e.Command.Category);
                node.Explanations.Add(EngineGameWords.Label(game) + ": \"" + EngineActionInfo.Readable(e.Command.Game, e.Command.Id, e.Command.Title) + "\"" +
                                      (category.Length > 0 ? " [" + category + "]" : string.Empty) + " (function map: " + functionName + "; " + EngineGameWords.Label(game) +
                                      " has no catalog row for it); " + hit.Reason);
                added = true;
            }
            return added;
        }

        // ------------------------------------------------------------------------------------------------------
        // Local model: candidates, prompt, grammar, validation
        // ------------------------------------------------------------------------------------------------------

        // Aircraft the examples prefer: well known, so the model recognises them and the database files are cached.
        private static readonly string[] ExamplePlanes =
        {
            "F-16C_50", "FA-18C_hornet", "A-10C II", "F-14B", "F-15ESE", "AH-64D_BLK_II_PLT", "UH-1H", "Ka-50_3", "Mi-24P", "AV8BNA", "M-2000C",
            "P-51D", "Bf-109K-4", "SpitfireLFMkIX", "FW-190D9", "MSFS2024", "IL2Game", "FalconBMS", "WarThunder", "XPlane12"
        };

        private static Selection SelectionOf(Catalog catalog, Concept concept, Variant variant, string why)
        {
            return new Selection { Concept = concept, Variant = variant, Label = variant.Label, Slots = variant.Slots.Where(catalog.Slots.ContainsKey).ToList(), Factor = 1.0, Why = why };
        }

        private static Concept ConceptOfSlot(Vocabulary v, string slotId, out Variant variant)
        {
            foreach (Concept concept in v.Concepts)
            {
                variant = concept.Variants.FirstOrDefault(va => va.Slots.Count == 1 && va.Slots[0] == slotId);
                if (variant != null)
                    return concept;
            }
            variant = null;
            return null;
        }

        internal static void BuildCandidates(string request, Vocabulary v, Catalog catalog, Understanding u, int maxFunctions, int maxScopes, out List<Candidate> functions, out List<Candidate> scopes)
        {
            BuildCandidates(request, v, catalog, u, new PromptLimits { MaxFunctions = maxFunctions, MaxScopes = maxScopes }, out functions, out scopes);
        }

        // Functions: what the rules read, every direction of the named functions, their relatives ("flares": the whole
        // CMS hat, chaff), then catalog functions whose names, descriptions or cockpit controls share words with the
        // request. Scopes: all, the rules' reading, classes, games, then single aircraft (named-like ones first).
        internal static void BuildCandidates(string request, Vocabulary v, Catalog catalog, Understanding u, PromptLimits limits, out List<Candidate> functions, out List<Candidate> scopes)
        {
            functions = new List<Candidate>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<Selection> pool = new List<Selection>(u.Selections);
            List<Concept> named = u.Spans.Where(s => s.Hit.Kind == HitKind.Function).Select(s => s.Hit.Concept).Distinct().ToList();
            foreach (Concept concept in named)
            {
                foreach (Variant variant in concept.Variants)
                    pool.Add(SelectionOf(catalog, concept, variant, "function word"));
            }
            foreach (Concept concept in named.SelectMany(c => c.Related).Distinct().Select(id => v.Concepts.FirstOrDefault(c => c.Id == id)).Where(c => c != null && !named.Contains(c)).ToList())
            {
                foreach (Variant variant in concept.Variants)
                    pool.Add(SelectionOf(catalog, concept, variant, "related function"));
            }
            // Words that named games, classes or aircraft say nothing about the function.
            HashSet<int> scopeWords = new HashSet<int>(u.Spans.Where(s => s.Hit.Kind != HitKind.Function).SelectMany(s => Enumerable.Range(s.Start, s.Length)));
            // When the rules understood every word, the named functions and their relatives are the whole choice; a
            // word search would only add noise ("speed brake" -> "speed limiter").
            bool understood = u.Selections.Count > 0 && u.NotUnderstood.Count == 0 && !u.UsedRetrieval;
            List<string> query = understood
                ? new List<string>()
                : u.Tokens.Where((w, i) => !scopeWords.Contains(i) && !v.Stopwords.Contains(w) && !v.Exclusions.Contains(w)).ToList();
            List<KeyValuePair<string, double>> rankedSlots = RankSlots(query, v, catalog);
            double topScore = rankedSlots.Count > 0 ? rankedSlots[0].Value : 0;
            foreach (KeyValuePair<string, double> ranked in rankedSlots.Where(r => r.Value >= topScore * 0.4).Take(Math.Max(4, limits.MaxFunctions)))
            {
                Variant variant;
                Concept concept = ConceptOfSlot(v, ranked.Key, out variant);
                pool.Add(new Selection { Concept = concept, Variant = variant, Label = SlotLabel(v, catalog.Slots[ranked.Key]), Slots = new List<string> { ranked.Key }, Factor = RetrievalFactor, Why = "catalog words" });
            }
            // The examples come from the aircraft and games the request names first ("landing gear in Star Citizen":
            // Star Citizen's own command), then the well-known ones.
            HashSet<string> preferred = u.Include.Count > 0 && u.Include.All(c => c.Games.Count > 0 || c.Aircraft.Count > 0)
                ? new HashSet<string>(ResolveClauses(v, catalog, u.Include, false).Keys, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Selection selection in pool)
            {
                if (functions.Count >= limits.MaxFunctions)
                    break;
                if (selection.Slots.Count == 0 || !seen.Add(string.Join("|", selection.Slots)))
                    continue;
                functions.Add(new Candidate
                {
                    Label = selection.Label,
                    Group = FunctionHeading(selection.Concept),
                    Detail = FunctionDetail(v, catalog, selection, Math.Max(limits.ExamplesPerFunction, preferred.Count > 0 ? 1 : 0), preferred),
                    Selection = selection
                });
            }
            // The prompt prints the directions of one function under its heading; the ids follow that order.
            functions = functions.GroupBy(f => f.Group).SelectMany(g => g).ToList();
            for (int i = 0; i < functions.Count; i++)
                functions[i].Id = "F" + (i + 1).ToString(CultureInfo.InvariantCulture);
            scopes = BuildScopes(request, v, catalog, u, limits.MaxScopes);
        }

        private static string FunctionHeading(Concept concept)
        {
            if (concept == null)
                return "Other catalog functions with words of the request";
            string about = concept.About.Length > 0 ? ": " + concept.About : string.Empty;
            List<string> inside = new List<string>();
            if (concept.System.Length > 0)
                inside.Add(concept.System);
            string aka = AlsoCalled(concept.Label, concept.Terms, 4);
            if (aka.Length > 0)
                inside.Add("also: " + aka);
            return concept.Label + (inside.Count > 0 ? " (" + string.Join("; ", inside) + ")" : string.Empty) + about;
        }

        // Other words people use for it ("gunship" for attack helicopters), without the label's own words.
        private static string AlsoCalled(string label, IEnumerable<string> terms, int max)
        {
            string own = Phrase(label);
            return string.Join(", ", terms.Where(t => t != own && !own.Contains(t) && !t.Contains(own) && t.Length > 2).Take(max));
        }

        // "button; 38 aircraft; e.g. F-16C_50 "CMS Switch - FWD" (stick: CMS forward), AH-64D_BLK_II_PLT "Flare Button" (cyclic: Flare button)"
        private static string FunctionDetail(Vocabulary v, Catalog catalog, Selection selection, int examples, HashSet<string> preferred)
        {
            EngineAutoGroup.Slot slot = catalog.Slots[selection.Slots[0]];
            List<RowInfo> rows = selection.Slots.SelectMany(s =>
            {
                List<RowInfo> list;
                return catalog.RowsBySlot.TryGetValue(s, out list) ? list : new List<RowInfo>();
            }).Where(r => !string.Equals(r.Row.Status, "substitute", StringComparison.OrdinalIgnoreCase)).ToList();
            int planes = rows.Select(r => r.Row.Plane).Where(p =>
            {
                EngineAutoGroup.Aircraft info;
                return catalog.Aircraft.TryGetValue(p, out info) && info.VariantOf.Length == 0 && info.Type != "layer";
            }).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            List<string> parts = new List<string> { slot.Axis ? "axis" : "button", planes.ToString(CultureInfo.InvariantCulture) + " aircraft" };
            if (selection.Concept == null)
                parts.Insert(0, "catalog function '" + SplitCamel(slot.Name).Replace("__", " ").Replace('_', ' ').Trim() + "'");
            if (examples > 0)
            {
                Dictionary<string, int> frequency = rows.GroupBy(r => r.Control).ToDictionary(g => g.Key, g => g.Count());
                List<string> shown = new List<string>();
                HashSet<string> controls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (RowInfo row in rows
                             .Where(r => catalog.Aircraft.ContainsKey(r.Row.Plane) && catalog.Aircraft[r.Row.Plane].VariantOf.Length == 0)
                             .OrderBy(r => preferred != null && preferred.Contains(r.Row.Plane) ? 0 : 1)
                             .ThenBy(r => { int i = Array.IndexOf(ExamplePlanes, r.Row.Plane); return i < 0 ? 100 : i; })
                             .ThenByDescending(r => frequency[r.Control])
                             .ThenBy(r => r.Row.Plane, StringComparer.OrdinalIgnoreCase))
                {
                    if (shown.Count >= examples)
                        break;
                    if (!controls.Add(row.Control.Length > 0 ? row.Control : row.Row.Action))
                        continue;
                    CommandInfo command = LookupCommand(row.Row.Game, row.Row.Plane, row.Row.Action);
                    if (EngineAutoGroup.IsDcs(row.Row.Game) && command == null)
                        continue;
                    string title = command != null && command.Title.Length > 0 ? "\"" + Shorten(EngineActionInfo.Readable(EngineAutoGroup.IsDcs(row.Row.Game) ? "DCS" : row.Row.Game, row.Row.Action, command.Title), 60) + "\"" : string.Empty;
                    string control = row.Control.Length > 0 ? Shorten(row.Control, 50) : string.Empty;
                    string where = EngineAutoGroup.IsDcs(row.Row.Game) ? row.Row.Plane : GameLabel(v, row.Row.Game);
                    shown.Add(where + " " + (title.Length > 0 ? title + (control.Length > 0 ? " (" + control + ")" : string.Empty) : control));
                }
                if (shown.Count > 0)
                    parts.Add("e.g. " + string.Join(", ", shown));
            }
            return string.Join("; ", parts);
        }

        private static string Shorten(string text, int max)
        {
            string value = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max - 3).TrimEnd() + "...";
        }

        private static List<Candidate> BuildScopes(string request, Vocabulary v, Catalog catalog, Understanding u, int maxScopes)
        {
            List<Candidate> scopes = new List<Candidate>();
            Action<Candidate> add = candidate =>
            {
                if (!scopes.Any(s => string.Equals(s.Label, candidate.Label, StringComparison.OrdinalIgnoreCase)))
                    scopes.Add(candidate);
            };
            add(new Candidate { Label = "all aircraft and games", Everything = true, Kind = "everything" });
            foreach (Clause clause in u.Include.Concat(u.Exclude))
            {
                if (clause.Label.Length > 0)
                    add(new Candidate { Label = clause.Label, Clauses = new List<Clause> { clause }, Kind = "request", Detail = Count(v, catalog, clause) + "; as the rules read the request" });
            }
            foreach (CategoryWord category in v.Categories)
            {
                Clause clause = new Clause();
                clause.Categories.Add(category);
                string count = Count(v, catalog, clause);
                string aka = AlsoCalled(category.Label, category.Terms, 3);
                if (count.Length > 0)
                    add(new Candidate { Label = category.Label, Clauses = new List<Clause> { clause }, Kind = "class", Detail = count + (aka.Length > 0 ? "; also: " + aka : string.Empty) });
            }
            HashSet<string> games = new HashSet<string>(catalog.Aircraft.Values.Select(GameOf), StringComparer.OrdinalIgnoreCase);
            foreach (GameWord game in v.Games.Where(g => games.Contains(g.Id) || g.Members.Any(games.Contains)))
            {
                Clause clause = new Clause();
                clause.Games.Add(game);
                bool dcs = EngineAutoGroup.IsDcs(game.Id);
                string aka = AlsoCalled(game.Label, game.Terms, 2);
                add(new Candidate
                {
                    Label = game.Label,
                    Clauses = new List<Clause> { clause },
                    Kind = "game",
                    Detail = (dcs ? Count(v, catalog, clause) + " with their own binds" : "one set of binds for all its aircraft") + (aka.Length > 0 ? "; also: " + aka : string.Empty)
                });
            }
            // Single DCS aircraft; those sharing a word or number with the request first ("F/A-18" -> FA-18C_hornet).
            // The others only when some words were not understood (they may name an aircraft the rules do not know).
            // Words the rules read as a function, class or game do not count ("jet" is no hint for "electric jet").
            HashSet<int> otherWords = new HashSet<int>(u.Spans.Where(s => s.Hit.Kind != HitKind.Aircraft).SelectMany(s => Enumerable.Range(s.Start, s.Length)));
            HashSet<string> words = new HashSet<string>(u.Tokens.Where((w, i) => !otherWords.Contains(i) && !v.Stopwords.Contains(w) && (w.Length >= 2 || char.IsDigit(w[0]))), StringComparer.Ordinal);
            bool unknownWords = u.NotUnderstood.Count > 0;
            var planes = catalog.Aircraft
                .Where(p => EngineAutoGroup.IsDcs(p.Value.Game) && p.Value.VariantOf.Length == 0 && p.Value.Type != "layer" && p.Value.Type != "ground")
                .Select(p =>
                {
                    List<string> nicks;
                    v.Nicknames.TryGetValue(p.Key, out nicks);
                    IEnumerable<string> names = Tokenize(p.Key).Concat((nicks ?? new List<string>()).SelectMany(Tokenize));
                    return new { Plane = p, Score = names.Distinct().Count(words.Contains) };
                })
                .Where(x => x.Score > 0 || unknownWords)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Plane.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Plane)
                .ToList();
            foreach (KeyValuePair<string, EngineAutoGroup.Aircraft> plane in planes)
            {
                Clause clause = new Clause();
                clause.Aircraft.Add(plane.Key);
                clause.AircraftWords.Add(plane.Key);
                add(new Candidate { Label = plane.Key, Clauses = new List<Clause> { clause }, Kind = "aircraft", Detail = AircraftDetail(v, plane.Key, plane.Value) });
            }
            if (scopes.Count > maxScopes)
                scopes = scopes.Take(Math.Max(1, maxScopes)).ToList();
            for (int i = 0; i < scopes.Count; i++)
                scopes[i].Id = "S" + (i + 1).ToString(CultureInfo.InvariantCulture);
            return scopes;
        }

        private static string Count(Vocabulary v, Catalog catalog, Clause clause)
        {
            int count = ResolveClauses(v, catalog, new List<Clause> { clause }, false).Keys.Count(k => catalog.Aircraft[k].VariantOf.Length == 0 && catalog.Aircraft[k].Type != "layer");
            return count == 0 ? string.Empty : count.ToString(CultureInfo.InvariantCulture) + " aircraft";
        }

        // "F-16C_50 (DCS; viper, fighting falcon; jet, multirole, modern)"
        private static string AircraftDetail(Vocabulary v, string plane, EngineAutoGroup.Aircraft info)
        {
            List<string> parts = new List<string> { "DCS" };
            List<string> nicks;
            if (v.Nicknames.TryGetValue(plane, out nicks) && nicks.Count > 0)
                parts.Add(string.Join(", ", nicks.Take(3)));
            List<string> kind = new List<string>();
            HashSet<string> tags;
            if (v.Tags.TryGetValue(plane, out tags))
                kind.AddRange(tags.OrderBy(t => t, StringComparer.Ordinal));
            else if (info.Type.Length > 0)
                kind.Add(info.Type.Replace('_', ' '));
            string role = LookupRole(plane);
            if (role.Length > 0 && !kind.Contains(role))
                kind.Add(role.Replace('_', ' '));
            if (info.Era.Length > 0)
                kind.Add(info.Era.Replace('_', ' '));
            if (kind.Count > 0)
                parts.Add(string.Join(", ", kind));
            return string.Join("; ", parts);
        }

        private static string RulesSummary(Understanding u)
        {
            string functions = u.Selections.Count == 0 ? "no function" : string.Join(", ", u.Selections.Select(s => s.Label));
            string scope = ScopeLabel(u);
            return functions + " for " + (scope.Length > 0 ? scope : "all aircraft and games") +
                   (u.NotUnderstood.Count > 0 ? "; words not understood: " + string.Join(", ", u.NotUnderstood) : string.Empty);
        }

        internal static string BuildUserPrompt(string request, Understanding u, List<Candidate> functions, List<Candidate> scopes)
        {
            string text = (request ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\"", "'").Trim();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Request: \"" + text + "\"");
            if (u != null)
                sb.AppendLine("Rule-based reading (may be incomplete or wrong): " + RulesSummary(u));
            sb.AppendLine();
            sb.AppendLine("Functions (heading = function, cockpit system and what it does; F lines = its directions):");
            string group = null;
            foreach (Candidate f in functions)
            {
                if (f.Group != group)
                {
                    sb.AppendLine("# " + f.Group);
                    group = f.Group;
                }
                sb.AppendLine(f.Id + " " + f.Label + " [" + f.Detail + "]");
            }
            sb.AppendLine();
            sb.AppendLine("Aircraft, classes and games:");
            foreach (Candidate s in scopes)
                sb.AppendLine(s.Id + " " + ScopeLine(s));
            sb.AppendLine();
            sb.Append("Request: \"" + text + "\"");
            return sb.ToString();
        }

        private static string ScopeLine(Candidate s)
        {
            switch (s.Kind)
            {
                case "everything": return "all aircraft and games";
                case "request": return "request: " + s.Label + (s.Detail.Length > 0 ? " (" + s.Detail + ")" : string.Empty);
                case "aircraft": return "aircraft: " + s.Label + (s.Detail.Length > 0 ? " (" + s.Detail + ")" : string.Empty);
                default: return s.Kind + ": " + s.Label + (s.Detail.Length > 0 ? " (" + s.Detail + ")" : string.Empty);
            }
        }

        // GBNF: the answer can only be the JSON object with ids of the lists above. The note comes first: one sentence
        // of what the model understood before it picks ids.
        internal static string BuildGrammar(List<Candidate> functions, List<Candidate> scopes)
        {
            int moreFunctions = Math.Max(0, Math.Min(11, functions.Count - 1));
            int moreScopes = Math.Max(0, Math.Min(11, scopes.Count - 1));
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("root ::= \"{\" ws \"\\\"note\\\":\" ws note \",\" ws \"\\\"functions\\\":\" ws fids \",\" ws \"\\\"include\\\":\" ws sids \",\" ws \"\\\"exclude\\\":\" ws sidsopt ws \"}\"");
            sb.AppendLine("fids ::= \"[\" ws fid (ws \",\" ws fid){0," + moreFunctions.ToString(CultureInfo.InvariantCulture) + "} ws \"]\"");
            sb.AppendLine("sids ::= \"[\" ws sid (ws \",\" ws sid){0," + moreScopes.ToString(CultureInfo.InvariantCulture) + "} ws \"]\"");
            sb.AppendLine("sidsopt ::= \"[\" ws \"]\" | sids");
            sb.AppendLine("fid ::= " + string.Join(" | ", functions.Select(f => "\"\\\"" + f.Id + "\\\"\"")));
            sb.AppendLine("sid ::= " + string.Join(" | ", scopes.Select(s => "\"\\\"" + s.Id + "\\\"\"")));
            sb.AppendLine("note ::= \"\\\"\" [^\"\\\\\\n]{0,160} \"\\\"\"");
            sb.AppendLine("ws ::= [ \\t\\n]{0,4}");
            return sb.ToString();
        }

        // The prompt for one model: system prompt, few-shot examples and the candidate lists, shrunk until it fits the
        // model's context (minus the answer): fewer command examples, fewer few-shot examples, fewer single aircraft,
        // fewer functions.
        internal static List<EngineLlm.Message> BuildMessages(EngineLlm.ModelInfo model, EngineLlm.Catalog llmCatalog, string request, Vocabulary v, Catalog catalog, Understanding u,
            out List<Candidate> functions, out List<Candidate> scopes, out int tokens)
        {
            PromptLimits limits = new PromptLimits
            {
                MaxFunctions = Math.Max(4, model.MaxFunctions),
                MaxScopes = Math.Max(8, model.MaxScopes),
                ExamplesPerFunction = Math.Max(0, model.ExamplesPerFunction),
                FewShot = Math.Max(0, model.FewShot)
            };
            int budget = Math.Max(512, model.ContextSize - model.MaxTokens - 64);
            List<EngineLlm.Message> messages = null;
            functions = scopes = null;
            tokens = 0;
            for (int attempt = 0; attempt < 16; attempt++)
            {
                BuildCandidates(request, v, catalog, u, limits, out functions, out scopes);
                messages = EngineLlm.Conversation(llmCatalog, limits.FewShot, BuildUserPrompt(request, u, functions, scopes));
                tokens = EngineLlm.EstimateTokens(messages);
                if (tokens <= budget)
                    break;
                if (limits.ExamplesPerFunction > 1)
                    limits.ExamplesPerFunction--;
                else if (limits.FewShot > 1)
                    limits.FewShot--;
                else if (limits.MaxScopes > 40)
                    limits.MaxScopes = Math.Max(40, limits.MaxScopes * 3 / 4);
                else if (limits.ExamplesPerFunction > 0)
                    limits.ExamplesPerFunction = 0;
                else if (limits.FewShot > 0)
                    limits.FewShot = 0;
                else if (limits.MaxFunctions > 8)
                    limits.MaxFunctions = Math.Max(8, limits.MaxFunctions * 3 / 4);
                else if (limits.MaxScopes > 20)
                    limits.MaxScopes = Math.Max(20, limits.MaxScopes * 3 / 4);
                else
                    break;
            }
            return messages;
        }

        // The prompt for the named model (--prompt-model), else the chosen or recommended one, else the largest catalog model.
        private static void ShowPrompt(Options options, Vocabulary v, Catalog catalog, Understanding u, EngineNlGroupResult result)
        {
            EngineLlm.Catalog llmCatalog = EngineLlm.LoadCatalog();
            EngineLlm.Settings settings = EngineLlm.LoadSettings();
            EngineLlm.ModelInfo model;
            string wanted = (options.PromptModel ?? string.Empty).Trim();
            if (string.Equals(wanted, EngineLlm.LocalModelId, StringComparison.OrdinalIgnoreCase))
                model = EngineLlm.LocalModel(settings);
            else if (wanted.Length > 0)
                model = llmCatalog.Models.FirstOrDefault(m => string.Equals(m.Id, wanted, StringComparison.OrdinalIgnoreCase));
            else
                model = EngineLlm.SelectedModel(llmCatalog, settings, EngineLlm.Recommend(EngineLlm.DetectHardware(), llmCatalog)) ?? llmCatalog.Models.LastOrDefault();
            if (model == null)
            {
                result.Warnings.Add("No such model for --prompt-model: " + wanted + ".");
                return;
            }
            List<Candidate> functions;
            List<Candidate> scopes;
            int tokens;
            List<EngineLlm.Message> messages = BuildMessages(model, llmCatalog, options.Request, v, catalog, u, out functions, out scopes, out tokens);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Model: " + model.Id + " (context " + model.ContextSize.ToString(CultureInfo.InvariantCulture) + ", about " + tokens.ToString(CultureInfo.InvariantCulture) + " prompt tokens, " +
                          functions.Count.ToString(CultureInfo.InvariantCulture) + " functions, " + scopes.Count.ToString(CultureInfo.InvariantCulture) + " aircraft lines)");
            foreach (EngineLlm.Message message in messages)
            {
                sb.AppendLine("=== " + message.Role);
                sb.AppendLine(message.Content);
            }
            sb.AppendLine("=== grammar");
            sb.Append(BuildGrammar(functions, scopes));
            result.Llm.Prompt = sb.ToString();
            result.Llm.FunctionCandidates = functions.Count;
            result.Llm.ScopeCandidates = scopes.Count;
            result.Llm.PromptTokens = tokens;
        }

        private static LlmChoice AskModel(string request, Vocabulary v, Catalog catalog, Understanding u, EngineNlGroupResult result)
        {
            EngineLlm.ModelInfo model;
            string problem;
            if (!EngineLlm.TryGetUsableModel(out model, out problem))
            {
                if (result.Llm.Mode == "on")
                    result.Errors.Add(problem);
                else
                    result.Llm.Note = problem;
                return null;
            }
            List<Candidate> functions;
            List<Candidate> scopes;
            int tokens;
            List<EngineLlm.Message> messages = BuildMessages(model, EngineLlm.LoadCatalog(), request, v, catalog, u, out functions, out scopes, out tokens);
            result.Llm.FunctionCandidates = functions.Count;
            result.Llm.ScopeCandidates = scopes.Count;
            result.Llm.PromptTokens = tokens;
            if (functions.Count == 0)
            {
                result.Llm.Note = "No candidate functions for the model.";
                return null;
            }
            EngineLlm.CallResult call = EngineLlm.Complete(model, messages, BuildGrammar(functions, scopes));
            result.Llm.Used = true;
            result.Llm.Model = model.Id;
            result.Llm.ProcessId = call.ProcessId;
            result.Llm.ProcessExited = call.ProcessExited;
            result.Llm.DurationMs = call.DurationMs;
            result.Llm.Answer = call.Content ?? string.Empty;
            if (!string.IsNullOrEmpty(call.Error))
            {
                result.Warnings.Add("Local model failed (" + call.Error + "); using the offline rules.");
                return null;
            }
            return Validate(call.Content, v, catalog, functions, scopes, result);
        }

        // Every id must come from the candidate lists; everything else is dropped. Classes and games picked together
        // narrow each other like in a request ("warbirds" + "IL-2" = IL-2 warbirds); "all" counts only alone. Without a
        // valid function or aircraft the offline result stays.
        internal static LlmChoice Validate(string content, Vocabulary v, Catalog catalog, List<Candidate> functions, List<Candidate> scopes, EngineNlGroupResult result)
        {
            Dictionary<string, object> answer;
            try
            {
                answer = EngineSerialization.FromJson<Dictionary<string, object>>(ExtractJsonObject(content ?? string.Empty));
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Local model answer is not valid JSON (" + ex.Message + "); using the offline rules.");
                return null;
            }
            if (answer == null)
            {
                result.Warnings.Add("Local model gave no answer; using the offline rules.");
                return null;
            }
            LlmChoice choice = new LlmChoice();
            foreach (string id in Strings(Get(answer, "functions")))
            {
                Candidate candidate = functions.FirstOrDefault(f => f.Id == id);
                if (candidate == null || candidate.Selection.Slots.Any(s => !catalog.Slots.ContainsKey(s)))
                {
                    result.Llm.Dropped.Add(id);
                    continue;
                }
                if (choice.Selections.Any(s => s.Slots.SequenceEqual(candidate.Selection.Slots)))
                    continue;
                choice.Selections.Add(new Selection
                {
                    Concept = candidate.Selection.Concept,
                    Variant = candidate.Selection.Variant,
                    Label = candidate.Label,
                    Slots = candidate.Selection.Slots,
                    Factor = LlmFactor,
                    Why = "local model (" + id + ")"
                });
                result.Llm.Functions.Add(candidate.Label);
            }
            List<Clause> include = new List<Clause>();
            List<Clause> exclude = new List<Clause>();
            bool everything = false;
            foreach (bool excluding in new[] { false, true })
            {
                List<Clause> target = excluding ? exclude : include;
                Clause merged = null;
                foreach (string id in Strings(Get(answer, excluding ? "exclude" : "include")).Distinct())
                {
                    Candidate candidate = scopes.FirstOrDefault(s => s.Id == id);
                    if (candidate == null || candidate.Clauses.Any(c => c.Aircraft.Any(a => !catalog.Aircraft.ContainsKey(a))))
                    {
                        result.Llm.Dropped.Add(id);
                        continue;
                    }
                    (excluding ? result.Llm.Exclude : result.Llm.Include).Add(candidate.Label);
                    if (candidate.Everything)
                    {
                        if (!excluding)
                            everything = true;
                        continue;
                    }
                    if (candidate.Kind == "class" || candidate.Kind == "game")
                    {
                        if (merged == null)
                        {
                            merged = new Clause();
                            target.Add(merged);
                        }
                        foreach (Clause clause in candidate.Clauses)
                        {
                            merged.Games.AddRange(clause.Games.Where(g => !merged.Games.Contains(g)));
                            merged.Categories.AddRange(clause.Categories.Where(c => !merged.Categories.Contains(c)));
                        }
                        continue;
                    }
                    target.AddRange(candidate.Clauses);
                }
            }
            if (include.Count > 0)
                everything = false;
            string note = Text(answer, "note");
            if (note.Length > 0)
                result.Llm.Note = note;
            if (choice.Selections.Count == 0 || (include.Count == 0 && !everything))
            {
                result.Warnings.Add("Local model answer had no valid function or aircraft; using the offline rules.");
                return null;
            }
            choice.Scope = ResolveClauses(v, catalog, everything ? new List<Clause>() : include, true);
            choice.Excluded = ResolveClauses(v, catalog, exclude, false);
            string includeLabel = everything ? string.Empty : string.Join(", ", include.Select(c => c.Label).Where(l => l.Length > 0).Distinct());
            string excludeLabel = string.Join(", ", exclude.Select(c => c.Label).Where(l => l.Length > 0).Distinct());
            choice.ScopeLabel = excludeLabel.Length > 0 ? (includeLabel.Length > 0 ? includeLabel : "all") + " without " + excludeLabel : includeLabel;
            return choice;
        }

        internal static string ExtractJsonObject(string text)
        {
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            return start >= 0 && end > start ? text.Substring(start, end - start + 1) : text;
        }

        // ------------------------------------------------------------------------------------------------------
        // Vocabulary check (engine relations from-text --check)
        // ------------------------------------------------------------------------------------------------------

        public static EngineNlVocabularyReport CheckVocabulary()
        {
            EngineNlVocabularyReport report = new EngineNlVocabularyReport();
            Vocabulary v;
            Catalog catalog;
            if (!TryLoad(report.Errors, out v, out catalog))
                return report;
            report.Functions = v.Concepts.Count;
            report.Variants = v.Concepts.Sum(c => c.Variants.Count);
            report.Games = v.Games.Count;
            report.Categories = v.Categories.Count;
            report.AircraftNames = v.Aliases.Count;
            report.Terms = v.Index.Count;
            report.SlotsCovered = v.Concepts.SelectMany(c => c.Variants).SelectMany(va => va.Slots).Distinct().Count();
            report.Problems.AddRange(v.Problems);
            foreach (Concept concept in v.Concepts.Where(c => c.About.Length == 0 || c.System.Length == 0))
                report.Problems.Add("Function '" + concept.Id + "' has no function_info (system and what it does; the local model needs it).");
            foreach (GameWord game in v.Games)
            {
                if (!catalog.Aircraft.Values.Any(a => game.Covers(GameOf(a))))
                    report.Problems.Add("Game '" + game.Id + "' has no entry in aircraft.json.");
            }
            return report;
        }

        // ------------------------------------------------------------------------------------------------------
        // Output
        // ------------------------------------------------------------------------------------------------------

        public static string ToText(EngineNlGroupResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Group by description");
            sb.AppendLine("Request: " + result.Request);
            sb.AppendLine("Method: " + result.Method);
            sb.AppendLine("Understood Functions: " + string.Join("; ", result.Understood.Functions));
            sb.AppendLine("Understood Aircraft: " + result.Understood.Aircraft + " (" + result.Understood.AircraftCount.ToString(CultureInfo.InvariantCulture) + ")");
            sb.AppendLine("Not Understood: " + (result.NotUnderstood.Count == 0 ? "-" : string.Join(", ", result.NotUnderstood)));
            sb.AppendLine("Dry Run: " + result.DryRun);
            if (result.Llm.Used || result.Llm.Note.Length > 0)
            {
                sb.AppendLine("Local Model: " + (result.Llm.Used ? result.Llm.Model + ", process " + result.Llm.ProcessId.ToString(CultureInfo.InvariantCulture) +
                                                                  (result.Llm.ProcessExited ? " exited" : " STILL RUNNING") + ", " + result.Llm.DurationMs.ToString(CultureInfo.InvariantCulture) + " ms" : "not used") +
                              (result.Llm.Note.Length > 0 ? " (" + result.Llm.Note + ")" : string.Empty));
                if (result.Llm.Used)
                    sb.AppendLine("Local Model Picked: " + (result.Llm.Functions.Count > 0 ? string.Join(", ", result.Llm.Functions) : "-") + " for " +
                                  (result.Llm.Include.Count > 0 ? string.Join(", ", result.Llm.Include) : "-") +
                                  (result.Llm.Exclude.Count > 0 ? " without " + string.Join(", ", result.Llm.Exclude) : string.Empty) +
                                  " (" + result.Llm.FunctionCandidates.ToString(CultureInfo.InvariantCulture) + " functions, " + result.Llm.ScopeCandidates.ToString(CultureInfo.InvariantCulture) +
                                  " aircraft lines, about " + result.Llm.PromptTokens.ToString(CultureInfo.InvariantCulture) + " prompt tokens)");
                if (result.Llm.Dropped.Count > 0)
                    sb.AppendLine("Local Model Dropped Ids: " + string.Join(", ", result.Llm.Dropped));
            }
            if (result.Llm.Prompt.Length > 0)
            {
                sb.AppendLine("Local Model Prompt:");
                sb.AppendLine(result.Llm.Prompt);
            }
            sb.AppendLine("Relations Created: " + result.RelationsCreated.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Relations Extended: " + result.RelationsExtended.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Nodes Added: " + result.NodesAdded.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Relations:");
            foreach (EngineNlGroupProposal proposal in result.Proposals)
            {
                sb.AppendLine("  - " + proposal.Name + " (" + (proposal.IsAxis ? "axis" : "button") + ", " + (proposal.Existing ? "extended" : "new") +
                              (proposal.Selected ? string.Empty : ", not selected") + ", " + proposal.Nodes.Count.ToString(CultureInfo.InvariantCulture) + " nodes, +" +
                              proposal.NodesAdded.ToString(CultureInfo.InvariantCulture) + "): slots " + string.Join(", ", proposal.Slots));
                foreach (EngineNlGroupNode node in proposal.Nodes)
                {
                    sb.AppendLine("      " + node.Spec + "  [" + node.Confidence.ToString("0.00", CultureInfo.InvariantCulture) + "]" +
                                  (node.KeptIn.Count > 0 ? "  kept elsewhere: " + string.Join("; ", node.KeptIn.Select(k => k.Key + " in " + k.Value)) : string.Empty));
                    foreach (string explanation in node.Explanations)
                        sb.AppendLine("          " + explanation);
                }
                if (proposal.Missing.Count > 0)
                    sb.AppendLine("      Not in the catalog for: " + string.Join(", ", proposal.Missing.Take(25)) +
                                  (proposal.Missing.Count > 25 ? " ... and " + (proposal.Missing.Count - 25).ToString(CultureInfo.InvariantCulture) + " more" : string.Empty));
            }
            if (result.Kept.Count > 0)
            {
                sb.AppendLine("Kept:");
                foreach (string kept in result.Kept)
                    sb.AppendLine("  - " + kept);
            }
            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                foreach (string warning in result.Warnings)
                    sb.AppendLine("  - " + warning);
            }
            if (result.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                foreach (string error in result.Errors)
                    sb.AppendLine("  - " + error);
            }
            return sb.ToString();
        }

        public static string ToText(EngineNlVocabularyReport report)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Group by description: vocabulary");
            sb.AppendLine("Functions: " + report.Functions.ToString(CultureInfo.InvariantCulture) + " (" + report.Variants.ToString(CultureInfo.InvariantCulture) + " directions, " + report.SlotsCovered.ToString(CultureInfo.InvariantCulture) + " catalog slots)");
            sb.AppendLine("Games: " + report.Games.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Classes: " + report.Categories.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Aircraft Names: " + report.AircraftNames.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Terms: " + report.Terms.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Problems: " + report.Problems.Count.ToString(CultureInfo.InvariantCulture));
            foreach (string problem in report.Problems)
                sb.AppendLine("  - " + problem);
            foreach (string error in report.Errors)
                sb.AppendLine("Error: " + error);
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------------------------
        // JSON helpers (JavaScriptSerializer gives Dictionary<string, object> and object[]/ArrayList)
        // ------------------------------------------------------------------------------------------------------

        internal static object Get(Dictionary<string, object> item, string key)
        {
            object value;
            return item != null && item.TryGetValue(key, out value) ? value : null;
        }

        internal static string Text(Dictionary<string, object> item, string key)
        {
            object value = Get(item, key);
            return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
        }

        internal static IEnumerable<string> Strings(object value)
        {
            IEnumerable list = value as IEnumerable;
            if (list == null || value is string)
                yield break;
            foreach (object item in list)
            {
                if (item != null && !(item is Dictionary<string, object>))
                    yield return Convert.ToString(item, CultureInfo.InvariantCulture).Trim();
            }
        }

        internal static IEnumerable<Dictionary<string, object>> Objects(object value)
        {
            IEnumerable list = value as IEnumerable;
            if (list == null || value is string)
                yield break;
            foreach (object item in list)
            {
                Dictionary<string, object> dict = item as Dictionary<string, object>;
                if (dict != null)
                    yield return dict;
            }
        }
    }

    public sealed class EngineNlGroupNode
    {
        public string Spec { get; set; }
        public string Game { get; set; }
        public string Id { get; set; }
        public List<string> Planes { get; set; }
        public double Confidence { get; set; }
        public List<string> Explanations { get; set; }
        public Dictionary<string, string> KeptIn { get; set; }
        // Aircraft left out because the id means something else there (EngineIdMeanings.Guard): added switched off.
        public List<string> Excluded { get; set; }
        public List<string> ExcludedReasons { get; set; }

        public EngineNlGroupNode()
        {
            ExcludedReasons = new List<string>();
            Spec = string.Empty;
            Game = string.Empty;
            Id = string.Empty;
            Planes = new List<string>();
            Excluded = new List<string>();
            Explanations = new List<string>();
            KeptIn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class EngineNlGroupProposal
    {
        public string Name { get; set; }
        public string Function { get; set; }
        public List<string> Slots { get; set; }
        public bool IsAxis { get; set; }
        public bool Existing { get; set; }
        public bool Selected { get; set; }
        public int NodesAdded { get; set; }
        public string Why { get; set; }
        public List<EngineNlGroupNode> Nodes { get; set; }
        public List<string> Missing { get; set; }

        public EngineNlGroupProposal()
        {
            Name = string.Empty;
            Function = string.Empty;
            Why = string.Empty;
            Slots = new List<string>();
            Nodes = new List<EngineNlGroupNode>();
            Missing = new List<string>();
        }
    }

    public sealed class EngineNlUnderstood
    {
        public List<string> Functions { get; set; }
        public string Aircraft { get; set; }
        public int AircraftCount { get; set; }

        public EngineNlUnderstood()
        {
            Functions = new List<string>();
            Aircraft = string.Empty;
        }
    }

    public sealed class EngineNlLlmInfo
    {
        public string Mode { get; set; }
        public bool Used { get; set; }
        public string Model { get; set; }
        public int ProcessId { get; set; }
        public bool ProcessExited { get; set; }
        public long DurationMs { get; set; }
        public string Answer { get; set; }
        public string Note { get; set; }
        public List<string> Dropped { get; set; }
        // What went into the prompt: candidate counts and the estimated prompt size in tokens.
        public int FunctionCandidates { get; set; }
        public int ScopeCandidates { get; set; }
        public int PromptTokens { get; set; }
        public List<string> Functions { get; set; }
        public List<string> Include { get; set; }
        public List<string> Exclude { get; set; }
        public string Prompt { get; set; }

        public EngineNlLlmInfo()
        {
            Prompt = string.Empty;
            Mode = "off";
            Model = string.Empty;
            Answer = string.Empty;
            Note = string.Empty;
            Dropped = new List<string>();
            Functions = new List<string>();
            Include = new List<string>();
            Exclude = new List<string>();
        }
    }

    public sealed class EngineNlGroupResult
    {
        public string Request { get; set; }
        public string Method { get; set; }
        public bool DryRun { get; set; }
        public EngineNlUnderstood Understood { get; set; }
        public List<string> NotUnderstood { get; set; }
        public EngineNlLlmInfo Llm { get; set; }
        public int RelationsCreated { get; set; }
        public int RelationsExtended { get; set; }
        public int NodesAdded { get; set; }
        public List<EngineNlGroupProposal> Proposals { get; set; }
        public List<string> Kept { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public EngineNlGroupResult()
        {
            Request = string.Empty;
            Method = "offline rules";
            Understood = new EngineNlUnderstood();
            NotUnderstood = new List<string>();
            Llm = new EngineNlLlmInfo();
            Proposals = new List<EngineNlGroupProposal>();
            Kept = new List<string>();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }

    public sealed class EngineNlVocabularyReport
    {
        public int Functions { get; set; }
        public int Variants { get; set; }
        public int SlotsCovered { get; set; }
        public int Games { get; set; }
        public int Categories { get; set; }
        public int AircraftNames { get; set; }
        public int Terms { get; set; }
        public List<string> Problems { get; set; }
        public List<string> Errors { get; set; }

        public EngineNlVocabularyReport()
        {
            Problems = new List<string>();
            Errors = new List<string>();
        }
    }
}
