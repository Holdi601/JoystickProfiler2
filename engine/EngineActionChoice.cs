using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace JoyPro.Engine
{
    // One recommended action per game (DCS: per aircraft) and vehicle family for a function, the others explained in
    // one line (docs/relation-builder.md "Recommended action"). Games often have two to four actions for one function
    // (Elite "Pitch Axis" / "(Alternate)" / "(Landing)", MSFS "Elevator Axis" / "Elevator Set" / "Aileron / Elevator",
    // Arma 3 plane and helicopter pitch); the user cannot tell which is right. The rules are generic data
    // (DB\Functions\action-variants.json: alternate set, landing mode, trim, helicopter / plane, mouse, view, combined
    // axis, another seat ...), nothing is written per action:
    //   - the recommended action of a group: no variant rule fits, no word of its own the function does not explain,
    //     the function's canonical control (EngineFunctionMap.PrimaryRank), the higher score, the shorter title;
    //   - an alternative says what makes it one: the texts of its rules, else the words the game gives no meaning for
    //     ("variant ('Mc') - meaning not documented by the game"), else that the game does not say how it differs;
    //   - a helicopter action next to a plane action of the same game is recommended too, each with "for helicopters" /
    //     "for planes".
    internal static class EngineActionChoice
    {
        public const string FileName = "action-variants.json";

        internal sealed class Rule
        {
            public string Id = string.Empty;
            public List<string[]> Words = new List<string[]>();
            public Regex Pattern;
            public bool Demote;
            // The variant still is the function (another set or mode of the same control).
            public bool KeepsFunction;
            public string Family = string.Empty;
            public string Text = string.Empty;
        }

        internal sealed class Item
        {
            public string Game = string.Empty;
            public string Id = string.Empty;
            public string Title = string.Empty;
            public string Category = string.Empty;
            public List<string> Planes = new List<string>();
            // The group within a game: the function the action stands for here ("" = the list's one function).
            public string Function = string.Empty;
            // The words the function itself has (its name, synonyms, game terms, directions): they need no explanation.
            public HashSet<string> Known = new HashSet<string>(StringComparer.Ordinal);
            public int PrimaryRank = -1;
            public int Score;
            // Games / DCS aircraft the list's relation has an action for already (no recommendation there).
            public HashSet<string> Covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal bool HasOne;
            // The recommended action of its group (an alternative only).
            internal Item Best;
            // Worked out once per Mark: the direction it names and its words without the filler.
            internal string Dir;
            internal HashSet<string> Twin;

            // The function's general explanation fits it: no word the game leaves unexplained, no rule that makes it
            // something else (a view, a weapon, another seat).
            public bool FitsFunction
            {
                get { return Unknown.Count == 0 && Rules.All(r => !r.Demote || r.KeepsFunction); }
            }

            public bool Recommended;
            // "for helicopters" / "for planes" when a game has a recommended action of each.
            public string RecommendedFor = string.Empty;
            // An alternative: how it differs from the recommended action.
            public string Variant = string.Empty;
            // Key of the recommended action it falls under (its own for a recommended one).
            public string Group = string.Empty;
            public int Alternatives;

            internal List<Rule> Rules = new List<Rule>();
            internal List<string> Unknown = new List<string>();
            internal string FamilyId = string.Empty;

            public string Key
            {
                get { return Game + ":" + Id + ":" + string.Join("|", Planes); }
            }
        }

        private static readonly object Sync = new object();
        private static List<Rule> rules;
        private static Dictionary<string, string> families;
        private static Dictionary<string, string> functionTemplates;
        private static HashSet<string> spaceGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Words any action may have without being a variant: axis words, sides, filler.
        private static readonly HashSet<string> Plain = new HashSet<string>(EngineFunctionMap.Tokenize(
            "axis axes set control controls input analog analogue full abs absolute main all both common engine ex value position " +
            "left right up down forward back backward nose increase decrease plus minus the a an and of for to on off in out by"), StringComparer.Ordinal);

        internal static void Reset()
        {
            lock (Sync)
            {
                rules = null;
                families = null;
                functionTemplates = null;
            }
        }

        private static List<Rule> Rules()
        {
            lock (Sync)
            {
                if (rules != null)
                    return rules;
                rules = new List<Rule>();
                families = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                functionTemplates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string file = Path.Combine(EngineNlGroup.FunctionsFolder(), FileName);
                if (!File.Exists(file))
                    return rules;
                try
                {
                    Dictionary<string, object> root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(file)) as Dictionary<string, object>;
                    object list;
                    if (root != null && root.TryGetValue("rules", out list) && list is object[])
                    {
                        foreach (Dictionary<string, object> r in ((object[])list).OfType<Dictionary<string, object>>())
                        {
                            Rule rule = new Rule { Id = Text(r, "id"), Text = Text(r, "text"), Family = Text(r, "family"), Demote = Flag(r, "demote"), KeepsFunction = Flag(r, "keepsFunction") };
                            object words;
                            if (r.TryGetValue("words", out words) && words is object[])
                                rule.Words.AddRange(((object[])words).OfType<string>().Select(EngineFunctionMap.Tokenize).Where(t => t.Length > 0));
                            string pattern = Text(r, "pattern");
                            if (pattern.Length > 0)
                                rule.Pattern = new Regex(pattern, RegexOptions.CultureInvariant);
                            rules.Add(rule);
                        }
                    }
                    object fam;
                    if (root != null && root.TryGetValue("families", out fam) && fam is Dictionary<string, object>)
                    {
                        foreach (KeyValuePair<string, object> f in (Dictionary<string, object>)fam)
                            families[f.Key] = f.Value as string ?? string.Empty;
                    }
                    object space;
                    if (root != null && root.TryGetValue("spaceGames", out space) && space is object[])
                        spaceGames = new HashSet<string>(((object[])space).OfType<string>(), StringComparer.OrdinalIgnoreCase);
                    object tpl;
                    if (root != null && root.TryGetValue("functionTemplates", out tpl) && tpl is Dictionary<string, object>)
                    {
                        foreach (KeyValuePair<string, object> f in (Dictionary<string, object>)tpl)
                            functionTemplates[f.Key] = f.Value as string ?? string.Empty;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException)
                {
                    JoyPro.Logging.JoyProLog.Warn("ActionChoice", "Could not read " + file + ": " + ex.Message);
                }
                return rules;
            }
        }

        // The general explanation's template id for a function: null when the data names none (then its name picks
        // one), "" for none at all.
        internal static string TemplateOf(string functionId)
        {
            Rules();
            string id;
            lock (Sync)
                return functionTemplates != null && !string.IsNullOrEmpty(functionId) && functionTemplates.TryGetValue(functionId, out id) ? id : null;
        }

        private static bool Flag(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        private static string Text(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v is string ? (string)v : string.Empty;
        }

        // The words of a function of the function map (name, synonyms, game terms, directions).
        internal static HashSet<string> WordsOf(EngineFunctionMap.Function f)
        {
            HashSet<string> words = new HashSet<string>(StringComparer.Ordinal);
            if (f == null)
                return words;
            foreach (EngineFunctionMap.Phrase p in f.Phrases)
                words.UnionWith(p.Tokens);
            foreach (KeyValuePair<string[], string> d in f.DirTokens)
                words.UnionWith(d.Key);
            words.UnionWith(EngineFunctionMap.Tokenize(f.Name));
            return words;
        }

        // Marks the recommended action of every group and explains the others. Items of DCS are grouped per aircraft
        // (a row is recommended when it is the best for one of its aircraft).
        public static void Mark(IList<Item> items)
        {
            List<Rule> all = Rules();
            foreach (Item item in items)
            {
                string title = EngineFunctionMap.CleanTitle(item.Game, item.Title);
                string[] tokens = EngineFunctionMap.Tokenize(title);
                item.Dir = DirectionOf(tokens);
                item.Twin = new HashSet<string>(tokens.Where(t => !TwinFiller.Contains(t)), StringComparer.Ordinal);
                List<string> text = tokens.Concat(EngineFunctionMap.Tokenize(item.Category)).ToList();
                item.Rules = all.Where(r => Fits(r, text, title, item.Known)).ToList();
                Rule family = item.Rules.FirstOrDefault(r => r.Family.Length > 0);
                item.FamilyId = family == null || family.Family == "plane" ? string.Empty : family.Family;
                HashSet<string> explained = new HashSet<string>(item.Known, StringComparer.Ordinal);
                foreach (Rule r in item.Rules)
                    foreach (string[] w in r.Words)
                        explained.UnionWith(w);
                item.Unknown = tokens.Where(t => t.Length > 1 && !t.All(char.IsDigit) && !explained.Contains(t) && !Plain.Contains(t)).Distinct().ToList();
                item.Recommended = false;
                item.RecommendedFor = item.Variant = item.Group = string.Empty;
                item.Alternatives = 0;
                item.HasOne = false;
                item.Best = null;
            }
            Dictionary<string, List<Item>> groups = new Dictionary<string, List<Item>>(StringComparer.OrdinalIgnoreCase);
            foreach (Item item in items)
            {
                IEnumerable<string> places = item.Game == "DCS" && item.Planes.Count > 0 ? item.Planes : new List<string> { item.Game };
                foreach (string place in places)
                    // Opposite directions ("increase" / "decrease") are groups of their own: each gets its recommendation.
                    Add(groups, item.Game + "|" + place + "|" + item.Function + "~" + item.Dir + "|" + item.FamilyId, item);
            }
            Dictionary<Item, HashSet<Item>> under = new Dictionary<Item, HashSet<Item>>();
            Dictionary<string, HashSet<string>> familiesOf = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<Item, string> placeOf = new Dictionary<Item, string>();
            foreach (KeyValuePair<string, List<Item>> group in groups)
            {
                // The relation has its action for this game / aircraft already: nothing more is recommended there.
                string at = group.Key.Split('|')[1];
                if (group.Value.Any(i => i.Covered.Contains(at)))
                {
                    foreach (Item i in group.Value)
                        i.HasOne = true;
                    continue;
                }
                Item best = group.Value.OrderBy(i => i.Rules.Any(r => r.Demote) ? 1 : 0).ThenBy(i => i.Unknown.Count > 0 ? 1 : 0)
                    .ThenBy(i => i.PrimaryRank < 0 ? 1 : 0).ThenBy(i => i.PrimaryRank).ThenByDescending(i => i.Score)
                    .ThenBy(i => i.Title.Length).ThenBy(i => i.Id, StringComparer.Ordinal).First();
                best.Recommended = true;
                HashSet<Item> set;
                if (!under.TryGetValue(best, out set))
                    under[best] = set = new HashSet<Item>();
                foreach (Item other in group.Value.Where(i => i != best))
                {
                    if (other.Group.Length == 0)
                        other.Group = best.Key;
                    if (other.Best == null)
                        other.Best = best;
                    set.Add(other);
                }
                string place = group.Key.Substring(0, group.Key.LastIndexOf('|'));
                HashSet<string> fams;
                if (!familiesOf.TryGetValue(place, out fams))
                    familiesOf[place] = fams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                fams.Add(best.FamilyId);
                placeOf[best] = place;
            }
            foreach (Item item in items)
            {
                if (item.Recommended)
                {
                    item.Group = item.Key;
                    HashSet<Item> set;
                    item.Alternatives = under.TryGetValue(item, out set) ? set.Count(i => !i.Recommended) : 0;
                    string place;
                    HashSet<string> fams;
                    if (placeOf.TryGetValue(item, out place) && familiesOf.TryGetValue(place, out fams) && fams.Count > 1)
                        item.RecommendedFor = FamilyText(item.FamilyId.Length == 0 && spaceGames.Contains(item.Game) ? "space" : item.FamilyId);
                    continue;
                }
                item.Variant = Explain(item);
            }
        }

        private static string FamilyText(string family)
        {
            string text;
            Rules();
            return families != null && families.TryGetValue(family ?? string.Empty, out text) ? text : string.Empty;
        }

        private static string Explain(Item item)
        {
            // What makes it a variant; a vehicle family of its own ("for ground vehicles") says it too.
            List<string> texts = item.Rules.Where(r => (r.Demote || r.Family.Length > 0 && r.Family != "plane") && r.Text.Length > 0).Select(r => r.Text).Distinct().ToList();
            if (texts.Count > 0)
                return string.Join("; ", texts);
            // Words the game does not explain: only for a variant of the recommended action (its words and more, "Pitch
            // Axis Mc" next to "Pitch Axis"), not for another control the map groups with it.
            if (item.Unknown.Count > 0 && item.Best != null && !Extends(item, item.Best))
                return string.Empty;
            if (item.Unknown.Count > 0)
                return "variant ('" + string.Join("', '", item.Unknown.Take(3).Select(u => OriginalWord(item.Title, u))) + "') - meaning not documented by the game";
            if (item.HasOne)
                return "another action for the same thing; the relation has one for this game already";
            // Only a real twin of the recommended action gets the generic hint ("Elevator Set" next to "Elevator Axis");
            // a distinct control the function map merely groups with it ("Condition Lever 1 Axis" next to "Condition
            // Lever Axis") gets none: there is nothing to tell apart.
            return item.Best != null && !NearDuplicate(item, item.Best) ? string.Empty : "another action for the same thing; the game does not say how it differs";
        }

        // Words that do not tell two actions apart: "Elevator Set" / "Elevator Axis" / "Axis Elevator Set Ex1".
        private static readonly HashSet<string> TwinFiller = new HashSet<string>(StringComparer.Ordinal)
        {
            "axis", "set", "ex", "ex1", "value", "control", "command", "input", "the", "a", "of", "and"
        };

        // Opposite directions are never "the same thing": "Engine RPM INCREASE" / "DECREASE", gear up / down, light on /
        // off. Each pair: the words of one side, then of the other.
        private static readonly string[][][] OppositePairs =
        {
            new[] { new[] { "increase", "inc", "incr", "raise" }, new[] { "decrease", "dec", "decr", "lower" } },
            new[] { new[] { "up" }, new[] { "down", "dn" } },
            new[] { new[] { "on" }, new[] { "off" } },
            new[] { new[] { "left" }, new[] { "right" } },
            new[] { new[] { "plus" }, new[] { "minus" } },
            new[] { new[] { "open" }, new[] { "close", "closed" } },
            new[] { new[] { "extend", "extended" }, new[] { "retract", "retracted" } },
            new[] { new[] { "forward", "fwd" }, new[] { "backward", "back", "aft" } },
        };

        private static Dictionary<string, string> directionWords;

        // The direction an action names ("0+" / "0-" for increase / decrease ...), empty for none or several.
        internal static string Direction(string game, string title)
        {
            if (directionWords == null)
            {
                Dictionary<string, string> words = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int p = 0; p < OppositePairs.Length; p++)
                    for (int side = 0; side < 2; side++)
                        foreach (string w in OppositePairs[p][side])
                            foreach (string t in EngineFunctionMap.Tokenize(w).Concat(new[] { w }))
                                words[t] = p.ToString(CultureInfo.InvariantCulture) + (side == 0 ? "+" : "-");
                directionWords = words;
            }
            return DirectionOf(EngineFunctionMap.Tokenize(EngineFunctionMap.CleanTitle(game, title)));
        }

        private static string DirectionOf(IEnumerable<string> tokens)
        {
            if (directionWords == null)
                Direction(string.Empty, string.Empty);
            HashSet<string> found = new HashSet<string>(StringComparer.Ordinal);
            foreach (string token in tokens)
            {
                string d;
                if (directionWords.TryGetValue(token, out d))
                    found.Add(d);
            }
            return found.Count == 1 ? found.First() : string.Empty;
        }

        // Opposite directions of one pair.
        internal static bool Opposite(string game, string titleA, string titleB)
        {
            return OppositeDirs(Direction(game, titleA), Direction(game, titleB));
        }

        private static bool OppositeDirs(string a, string b)
        {
            return a.Length > 0 && b.Length > 0 && a[0] == b[0] && a.Substring(0, a.Length - 1) == b.Substring(0, b.Length - 1) && a != b;
        }

        // "twin" (the same thing: the generic hint is right), "opposite" (never the same thing) or "different".
        internal static string Relation(string game, string titleA, string titleB)
        {
            Item a = new Item { Game = game, Title = titleA };
            Item b = new Item { Game = game, Title = titleB };
            return Opposite(game, titleA, titleB) ? "opposite" : NearDuplicate(a, b) ? "twin" : "different";
        }

        private static HashSet<string> TwinWords(Item i)
        {
            if (i.Twin != null)
                return new HashSet<string>(i.Twin, StringComparer.Ordinal);
            return new HashSet<string>(EngineFunctionMap.Tokenize(EngineFunctionMap.CleanTitle(i.Game, i.Title)).Where(t => !TwinFiller.Contains(t)), StringComparer.Ordinal);
        }

        // Same words once the filler is gone: a twin of the recommended action.
        internal static bool NearDuplicate(Item a, Item b)
        {
            if (OppositeDirs(a.Dir ?? Direction(a.Game, a.Title), b.Dir ?? Direction(b.Game, b.Title)))
                return false;
            HashSet<string> x = TwinWords(a);
            return x.Count > 0 && x.SetEquals(TwinWords(b));
        }

        // a is b's control plus words the game does not explain ("Mc pitch" next to "Rpc pitch", "Fuel Selector 2 Left AUX"
        // next to "Fuel Selector 2 Left"): all its other words are b's.
        internal static bool Extends(Item a, Item b)
        {
            HashSet<string> rest = TwinWords(a);
            rest.ExceptWith(a.Unknown ?? new List<string>());
            return rest.Count > 0 && rest.IsSubsetOf(TwinWords(b));
        }

        // The word as the game writes it ("Mc" for the token "mc").
        private static string OriginalWord(string title, string token)
        {
            foreach (Match m in Regex.Matches(title ?? string.Empty, @"[\p{L}\p{Nd}]+"))
            {
                string[] t = EngineFunctionMap.Tokenize(m.Value);
                if (t.Length == 1 && t[0] == token)
                    return m.Value;
            }
            return token;
        }

        private static bool Fits(Rule rule, List<string> tokens, string title, HashSet<string> known)
        {
            if (rule.Pattern != null && rule.Pattern.IsMatch(title ?? string.Empty))
                return true;
            foreach (string[] words in rule.Words)
            {
                // The function's own words are no variant ("trim" for a trim relation, "cyclic" for the cyclic).
                if (words.All(known.Contains) && rule.Family.Length == 0)
                    continue;
                for (int i = 0; i + words.Length <= tokens.Count; i++)
                {
                    bool hit = true;
                    for (int j = 0; j < words.Length && hit; j++)
                        hit = tokens[i + j] == words[j];
                    if (hit)
                        return true;
                }
            }
            return false;
        }

        private static void Add(Dictionary<string, List<Item>> map, string key, Item item)
        {
            List<Item> list;
            if (!map.TryGetValue(key, out list))
                map[key] = list = new List<Item>();
            list.Add(item);
        }
    }
}
