using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    // The function knowledge map (DB/Functions/function-map.json): JoyPro's own offline "brain" for what an action does.
    // Every common flight, space and vehicle function has an id, a name, a category, English synonyms and game-specific
    // terms ("throttle" = "thrust" = "power lever" = "collective" in a helicopter = "strafe longitudinal" in Star Citizen).
    // At run time every action of every game list on this PC (EngineRelationSuggest.Index) is mapped to function ids:
    //   1. the manual-verified function catalog (DB/Functions/<Game>.tsv) where it knows the action (slot -> function);
    //   2. the map's rules for everything else: a synonym or game term in the title (or the game's readable id), the
    //      function's "not" words, axis or button, the aircraft class (helicopter, spaceship), with weights when several
    //      functions fit. New games and aircraft are covered by these rules, not by a fixed table.
    // Each mapping carries a direction ("up", "increase", "left" ...) so "gear up" and "gear down" stay apart.
    // Used by relations search / resolve / functions / suggest and Auto Group. No AI, deterministic, built once per run.
    internal static class EngineFunctionMap
    {
        public const string FileName = "function-map.json";

        internal sealed class Function
        {
            public string Id = string.Empty;
            public string Name = string.Empty;
            public string Category = string.Empty;
            // "axis", "button" or "both": the function's natural kind (ranks those actions first).
            public string Kind = "both";
            // "axis" / "button": only actions of that kind can be this function.
            public string Only = string.Empty;
            public string Description = string.Empty;
            public int Priority;
            // Axes of this function have a side (left / right engine, wheel brake).
            public bool Sided;
            // Axes of this function take a direction from its "dirs" too (TDC horizontal / vertical, look left-right).
            public bool AxisDirs;
            // "dirs" given: only those directions count (an empty list: the function has none, e.g. a trigger).
            public bool HasDirs;
            // The action's own key is its direction: "number" (PTT 1, OSB 12) or "label" (UFC key 7, A).
            public string Keyed = string.Empty;
            public List<string> Synonyms = new List<string>();
            public Dictionary<string, List<string>> Terms = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public List<string> Not = new List<string>();
            public Dictionary<string, List<string>> Dirs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public List<SlotLink> Slots = new List<SlotLink>();
            public List<string> Related = new List<string>();
            // Auto Group: the catalog slot an uncatalogued action of this function and direction goes to.
            public Dictionary<string, string> GroupSlots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Plain-English words for what it does ("release drop deploy flares decoys"): searched like a document, not
            // matched in action titles.
            public List<string> Keywords = new List<string>();
            // The function as a search document (BM25): token -> weighted count over name and synonyms (1.0), keywords
            // (0.8) and the description (0.4).
            internal readonly Dictionary<string, double> Doc = new Dictionary<string, double>(StringComparer.Ordinal);
            internal double DocLength;

            internal readonly List<Phrase> Phrases = new List<Phrase>();
            internal readonly List<Phrase> KeywordPhrases = new List<Phrase>();
            internal readonly List<string[]> NotTokens = new List<string[]>();
            internal readonly List<KeyValuePair<string[], string>> DirTokens = new List<KeyValuePair<string[], string>>();

            public bool AxisKind
            {
                get { return Kind == "axis"; }
            }
        }

        internal sealed class SlotLink
        {
            public string Slot = string.Empty;
            public string Dir = string.Empty;
            public double Weight = 1.0;
        }

        internal sealed class Phrase
        {
            public Function Function;
            public string Text = string.Empty;
            public string[] Tokens = new string[0];
            // "" = every game; a game id ("StarCitizen", "DCS") or "class:heli" / "class:space" / "class:ground".
            public string Scope = string.Empty;
            public bool IsName;
            // A described phrase ("go faster"): matched in queries only, never in action titles.
            public bool IsKeyword;
            // The words a query is compared with (without stopwords: "power to shields" = "power shields").
            public string[] QueryTokens = new string[0];
        }

        internal sealed class Assignment
        {
            public Function Function;
            public string Dir = string.Empty;
            public double Weight;
            // "catalog" (manual-verified function catalog) or "rule" (the map's synonyms and terms).
            public string Source = string.Empty;
            // The word or catalog slot that decided it ("collective", "slot throttleleft").
            public string Term = string.Empty;
            // The term is the function's name or a plain synonym in the title (not a game-specific term or the id).
            public bool InTitle;
        }

        internal sealed class Entry
        {
            public EngineRelationSuggest.Command Command;
            public List<Assignment> Functions = new List<Assignment>();
            public string[] TitleTokens = new string[0];
            public string Class = string.Empty;
            // PrimaryRank per function id, worked out once when a search asks.
            internal Dictionary<string, int> PrimaryRanks;

            public Assignment Of(string functionId)
            {
                foreach (Assignment a in Functions)
                {
                    if (string.Equals(a.Function.Id, functionId, StringComparison.OrdinalIgnoreCase))
                        return a;
                }
                return null;
            }
        }

        internal sealed class Map
        {
            public List<Function> Functions = new List<Function>();
            public Dictionary<string, Function> ById = new Dictionary<string, Function>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<Entry> Entries = new List<Entry>();
            public Dictionary<string, List<Entry>> ByFunction = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, Entry> ByKey = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            public List<KeyValuePair<string[], string>> GlobalDirs = new List<KeyValuePair<string[], string>>();
            public HashSet<string> DirectionWords = new HashSet<string>(StringComparer.Ordinal);
            public Dictionary<string, List<Phrase>> PhrasesByToken = new Dictionary<string, List<Phrase>>(StringComparer.Ordinal);
            public Dictionary<string, List<string>> ClassPlanes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<string>> ClassGames = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<string[]>> ClassWords = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> PlaneTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Stopwords = new HashSet<string>(StringComparer.Ordinal);
            // Filler of a described query ("make the plane go faster"): left out of the document search.
            public HashSet<string> QueryFiller = new HashSet<string>(StringComparer.Ordinal);
            // Words that mean much the same ("drop" ~ "release" ~ "deploy"): a query word also searches its neighbours.
            public Dictionary<string, List<string>> Lexicon = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            // How many function documents have a token (BM25), and their average length.
            public Dictionary<string, int> DocFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
            public double AverageDocLength = 1;
            // Every word JoyPro knows (titles, readable ids, function documents, lexicon) with its count: "did you mean".
            public Dictionary<string, int> Vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
            // Aircraft Auto Group leaves to the catalog (community mods the catalog skips on purpose).
            public HashSet<string> AutoGroupIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Warnings = new List<string>();
            public string Source = string.Empty;
            public long LoadMilliseconds;
            // Of LoadMilliseconds: reading the game lists (the rest is mapping them).
            public long IndexMilliseconds;
            public bool FromCache;
            public int Version;
        }

        private static Map cached;
        private static readonly object Sync = new object();

        // The map with every action of every game list on this PC, built once per engine run.
        public static Map Load(List<string> warnings)
        {
            lock (Sync)
            {
                if (cached == null)
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    Map map = LoadKnowledge(KnowledgeFile());
                    string fingerprint = EngineFunctionMapCache.Fingerprint(map);
                    List<EngineRelationSuggest.Command> commands;
                    if (EngineFunctionMapCache.TryRead(map, fingerprint, out commands))
                    {
                        map.FromCache = true;
                        map.IndexMilliseconds = watch.ElapsedMilliseconds;
                    }
                    else
                    {
                        commands = EngineRelationSuggest.Parse(map.Warnings);
                        map.IndexMilliseconds = watch.ElapsedMilliseconds;
                        BuildIndex(map, commands);
                        EngineFunctionMapCache.TryWrite(map, fingerprint, commands);
                    }
                    EngineRelationSuggest.SetIndex(commands);
                    AddTitleVocabulary(map);
                    watch.Stop();
                    map.LoadMilliseconds = watch.ElapsedMilliseconds;
                    cached = map;
                }
                if (warnings != null)
                    warnings.AddRange(cached.Warnings.Where(w => !warnings.Contains(w)));
                return cached;
            }
        }

        internal static void Reset()
        {
            lock (Sync)
            {
                cached = null;
                EngineRelationSuggest.ResetIndex();
                EngineIdMeanings.Reset();
            }
        }

        public static string KnowledgeFile()
        {
            string overridePath = Environment.GetEnvironmentVariable("JOYPRO_FUNCTION_MAP");
            if (!string.IsNullOrWhiteSpace(overridePath))
                return overridePath.Trim();
            return Path.Combine(EngineNlGroup.FunctionsFolder(), FileName);
        }

        // ---- Text ----

        private static readonly HashSet<string> NoStem = new HashSet<string>(StringComparer.Ordinal)
        {
            "axis", "afcs", "gps", "ils", "sas", "cms", "dms", "tms", "ecs", "ias", "tas", "fcs", "ccs", "pos", "less", "plus", "minus",
            "bus", "gas", "pass", "class", "this", "its", "has", "was", "status", "radius", "focus", "chassis", "lens", "us", "ais",
            "cas", "srs", "rws", "tws", "bvr", "hms", "jhmcs", "hmcs", "atis", "abs", "aws", "ews", "nvgs", "mfds", "ecms", "ms", "ics",
            "cds", "ins", "dis", "s", "ns", "is", "as", "ss", "aas", "ufcs", "mids", "atcs", "fcs", "cmds", "ews", "lws", "mws", "rws", "sss",
            "ldgs", "sams", "hms", "afcs"
        };

        // Lower case words: camelCase, letters/digits and punctuation split, '+' / '-' alone as "plus" / "minus", a plural
        // 's' dropped ("flaps" = "flap"), so titles, ids and queries compare the same way.
        internal static string[] Tokenize(string text)
        {
            if (string.IsNullOrEmpty(text))
                return new string[0];
            List<string> tokens = new List<string>();
            StringBuilder word = new StringBuilder(16);
            Action flush = () =>
            {
                if (word.Length > 0)
                {
                    tokens.Add(Stem(word.ToString()));
                    word.Clear();
                }
            };
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsLetterOrDigit(c))
                {
                    if (word.Length > 0)
                    {
                        char prev = text[i - 1];
                        bool split = (char.IsLower(prev) && char.IsUpper(c)) ||
                                     (char.IsUpper(prev) && char.IsUpper(c) && i + 1 < text.Length && char.IsLower(text[i + 1])) ||
                                     (char.IsLetter(prev) && char.IsDigit(c)) || (char.IsDigit(prev) && char.IsLetter(c));
                        if (split)
                            flush();
                    }
                    word.Append(char.ToLowerInvariant(c));
                    continue;
                }
                flush();
                if (c == '+')
                {
                    tokens.Add("plus");
                    continue;
                }
                if (c == '-')
                {
                    // "Zoom -", "Brake Axis -", "RNG-" and "-1" are minus; "F-16", "Throttle - DECREASE" and
                    // "Up-Left" are only separators.
                    int next = i + 1;
                    while (next < text.Length && text[next] == ' ')
                        next++;
                    bool atEnd = next >= text.Length || text[next] == ')';
                    bool beforeDigit = i + 1 < text.Length && char.IsDigit(text[i + 1]) && (i == 0 || text[i - 1] == ' ' || text[i - 1] == '(');
                    if (atEnd || beforeDigit)
                        tokens.Add("minus");
                }
            }
            flush();
            return tokens.ToArray();
        }

        internal static string Stem(string word)
        {
            if (word.Length > 3 && word[word.Length - 1] == 's' && !NoStem.Contains(word) && !word.EndsWith("ss", StringComparison.Ordinal) &&
                !word.EndsWith("us", StringComparison.Ordinal) && !word.EndsWith("is", StringComparison.Ordinal))
            {
                if (word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 4)
                    return word.Substring(0, word.Length - 3) + "y";
                return word.Substring(0, word.Length - 1);
            }
            return word;
        }

        // A title without the game's internal names in brackets ("Plane airbrake (HeliThrottleNeg, full axis)" is no
        // throttle), DCS module prefixes ("29Fulcrum:Inp:", "Input.Yak52.") and seat or panel tags ("[FWD]", "[SimPit]",
        // Falcon BMS "GEAR: ").
        internal static string CleanTitle(string game, string title)
        {
            if (string.IsNullOrEmpty(title))
                return string.Empty;
            string text = Regex.Replace(title, @"\(([^()]*)\)", m => Regex.IsMatch(m.Groups[1].Value, "[a-z][A-Z]") ? " " : m.Value);
            text = Regex.Replace(text, @"^\s*[0-9A-Za-z]+:Inp:", string.Empty);
            text = Regex.Replace(text, @"^\s*Input\.[A-Za-z0-9]+\.", string.Empty);
            text = Regex.Replace(text, @"^(\s*\[[^\]]*\])+", string.Empty);
            text = Regex.Replace(text, @"\[[A-Za-z][A-Za-z0-9]*\]\s*$", string.Empty);
            if (string.Equals(game, "FalconBMS", StringComparison.OrdinalIgnoreCase))
                text = Regex.Replace(text, @"^\s*[A-Z0-9][A-Z0-9 ]{1,14}:\s+", string.Empty);
            return text;
        }

        // Ids that read as words ("v_strafe_longitudinal", "KEY_THROTTLE_AXIS_SET_EX1", "ThrottleAxis", "axis/throttle_1").
        // DCS ids are opaque codes and never used.
        internal static string[] IdTokens(string game, string id)
        {
            if (string.IsNullOrEmpty(id) || game == "DCS")
                return new string[0];
            string text = id;
            int bar = text.LastIndexOf('|');
            if (bar >= 0)
                text = text.Substring(bar + 1);
            text = Regex.Replace(text, "^(v_|KEY_|ID_|INPUT_(RANGE|STATE|ACTION)_)", string.Empty, RegexOptions.IgnoreCase);
            return Tokenize(text.Replace('.', ' ').Replace('/', ' '));
        }

        // ---- Knowledge file ----

        internal static Map LoadKnowledge(string file)
        {
            Map map = new Map { Source = file };
            if (!File.Exists(file))
            {
                map.Warnings.Add("Function map missing: " + file + " (update JoyPro).");
                return map;
            }
            Dictionary<string, object> root;
            try
            {
                root = EngineSerialization.FromJson<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                map.Warnings.Add("Function map could not be read: " + ex.Message);
                return map;
            }
            map.Version = (int)Number(Get(root, "version"), 1);
            foreach (KeyValuePair<string, object> category in Dict(Get(root, "categories")))
                map.Categories[category.Key] = Convert.ToString(category.Value, CultureInfo.InvariantCulture);
            foreach (string word in Strings(Get(root, "stopwords")))
                map.Stopwords.Add(Stem(word.ToLowerInvariant()));
            foreach (string plane in Strings(Get(root, "autoGroupIgnore")))
                map.AutoGroupIgnore.Add(plane);
            foreach (string word in Strings(Get(root, "queryFiller")))
                map.QueryFiller.Add(Stem(word.ToLowerInvariant()));
            foreach (object raw in List(Get(root, "lexicon")))
            {
                List<string> group = Strings(raw).SelectMany(Tokenize).Distinct().ToList();
                foreach (string word in group)
                {
                    List<string> neighbours;
                    if (!map.Lexicon.TryGetValue(word, out neighbours))
                        map.Lexicon[word] = neighbours = new List<string>();
                    foreach (string other in group.Where(o => o != word && !neighbours.Contains(o)))
                        neighbours.Add(other);
                }
            }
            foreach (KeyValuePair<string, object> dir in Dict(Get(root, "directions")))
            {
                foreach (string alias in Strings(dir.Value))
                {
                    string[] tokens = Tokenize(alias);
                    map.GlobalDirs.Add(new KeyValuePair<string[], string>(tokens, dir.Key));
                    if (tokens.Length == 1)
                        map.DirectionWords.Add(tokens[0]);
                }
            }
            map.GlobalDirs.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            foreach (KeyValuePair<string, object> cls in Dict(Get(root, "classes")))
            {
                Dictionary<string, object> body = Dict(cls.Value);
                map.ClassPlanes[cls.Key] = Strings(Get(body, "planes")).ToList();
                map.ClassGames[cls.Key] = Strings(Get(body, "games")).ToList();
                map.ClassWords[cls.Key] = Strings(Get(body, "words")).Select(Tokenize).Where(t => t.Length > 0).ToList();
            }

            foreach (object raw in List(Get(root, "functions")))
            {
                Dictionary<string, object> item = Dict(raw);
                Function f = new Function
                {
                    Id = Text(item, "id"),
                    Name = Text(item, "name"),
                    Category = Text(item, "category"),
                    Kind = Text(item, "kind").Length > 0 ? Text(item, "kind").ToLowerInvariant() : "both",
                    Only = Text(item, "only").ToLowerInvariant(),
                    Description = Text(item, "desc"),
                    Priority = (int)Number(Get(item, "priority"), 0),
                    Sided = string.Equals(Text(item, "sided"), "true", StringComparison.OrdinalIgnoreCase),
                    AxisDirs = string.Equals(Text(item, "axisDirs"), "true", StringComparison.OrdinalIgnoreCase),
                    HasDirs = item.ContainsKey("dirs"),
                    Keyed = Text(item, "keyed").ToLowerInvariant()
                };
                if (f.Id.Length == 0)
                    continue;
                if (f.Name.Length == 0)
                    f.Name = f.Id;
                if (map.ById.ContainsKey(f.Id))
                {
                    map.Warnings.Add("Function map: duplicate id " + f.Id + ".");
                    continue;
                }
                f.Synonyms = Strings(Get(item, "synonyms")).ToList();
                f.Keywords = Strings(Get(item, "keywords")).ToList();
                foreach (KeyValuePair<string, object> scope in Dict(Get(item, "terms")))
                    f.Terms[scope.Key] = Strings(scope.Value).ToList();
                f.Not = Strings(Get(item, "not")).ToList();
                f.Related = Strings(Get(item, "related")).ToList();
                foreach (KeyValuePair<string, object> dir in Dict(Get(item, "dirs")))
                    f.Dirs[dir.Key] = Strings(dir.Value).ToList();
                foreach (KeyValuePair<string, object> slot in Dict(Get(item, "slots")))
                {
                    // "dir" or "dir@0.8"
                    string value = Convert.ToString(slot.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                    SlotLink link = new SlotLink { Slot = slot.Key };
                    int at = value.IndexOf('@');
                    if (at >= 0)
                    {
                        double w;
                        if (double.TryParse(value.Substring(at + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out w))
                            link.Weight = w;
                        value = value.Substring(0, at);
                    }
                    link.Dir = value.Trim();
                    f.Slots.Add(link);
                }
                foreach (KeyValuePair<string, object> slot in Dict(Get(item, "group")))
                    f.GroupSlots[slot.Key] = Convert.ToString(slot.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                Compile(map, f);
                map.Functions.Add(f);
                map.ById[f.Id] = f;
            }
            // "describe": function id -> plain-English phrases for what it does (the same as a function's "keywords").
            foreach (KeyValuePair<string, object> described in Dict(Get(root, "describe")))
            {
                Function f;
                if (map.ById.TryGetValue(described.Key, out f))
                    f.Keywords.AddRange(Strings(described.Value));
                else
                    map.Warnings.Add("Function map: 'describe' names an unknown function " + described.Key + ".");
            }
            BuildDocuments(map);
            return map;
        }

        // The functions as search documents (BM25 over name/synonyms/terms, keywords and the description).
        private static void BuildDocuments(Map map)
        {
            foreach (Function f in map.Functions)
            {
                // Described phrases of two words or more ("go faster", "talk tower") for the query's phrase pass.
                foreach (string keyword in f.Keywords)
                {
                    string[] tokens = Tokenize(keyword).Where(t => !map.Stopwords.Contains(t)).ToArray();
                    if (tokens.Length >= 2 && f.KeywordPhrases.All(p => !p.Tokens.SequenceEqual(tokens)))
                        f.KeywordPhrases.Add(new Phrase { Function = f, Text = keyword, Tokens = tokens, IsKeyword = true });
                }
                Action<IEnumerable<string>, double> add = (texts, weight) =>
                {
                    foreach (string text in texts)
                    {
                        foreach (string t in Tokenize(text).Where(x => !map.Stopwords.Contains(x) && !map.QueryFiller.Contains(x)).Distinct())
                        {
                            double current;
                            f.Doc.TryGetValue(t, out current);
                            // A word counts once per field, saturating: many synonyms with "throttle" do not make it a
                            // better throttle than a function named "Throttle".
                            f.Doc[t] = Math.Min(current + weight, 3.0);
                        }
                    }
                };
                add(new[] { f.Name, f.Id.Replace('_', ' ') }, 1.5);
                add(f.Synonyms, 1.0);
                add(f.Terms.Values.SelectMany(v => v), 0.6);
                add(f.Keywords, 0.8);
                add(new[] { f.Description }, 0.4);
                f.DocLength = Math.Max(1, f.Doc.Count);
                foreach (string t in f.Doc.Keys)
                    map.DocFrequency[t] = (map.DocFrequency.ContainsKey(t) ? map.DocFrequency[t] : 0) + 1;
            }
            map.AverageDocLength = map.Functions.Count == 0 ? 1 : map.Functions.Average(f => f.DocLength);
            foreach (string t in map.DocFrequency.Keys)
                map.Vocabulary[t] = map.Vocabulary.ContainsKey(t) ? map.Vocabulary[t] + 1 : 1;
            foreach (string t in map.Lexicon.Keys)
                map.Vocabulary[t] = map.Vocabulary.ContainsKey(t) ? map.Vocabulary[t] + 1 : 1;
        }

        // The words of every action title and readable id (for "did you mean"), once the index is there.
        private static void AddTitleVocabulary(Map map)
        {
            foreach (Entry entry in map.Entries)
            {
                foreach (string t in entry.TitleTokens)
                    map.Vocabulary[t] = map.Vocabulary.ContainsKey(t) ? map.Vocabulary[t] + 1 : 1;
            }
        }

        private static void Compile(Map map, Function f)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            Action<string, string, bool> add = (text, scope, isName) =>
            {
                string[] tokens = Tokenize(text);
                if (tokens.Length == 0 || !seen.Add(scope + "|" + string.Join(" ", tokens)))
                    return;
                Phrase phrase = new Phrase { Function = f, Text = text, Tokens = tokens, Scope = scope, IsName = isName };
                string[] queryTokens = tokens.Where(x => !map.Stopwords.Contains(x)).ToArray();
                // "power to shields" = "power shields" in a query; a phrase that would shrink to one word stays whole.
                phrase.QueryTokens = queryTokens.Length >= 2 ? queryTokens : tokens;
                f.Phrases.Add(phrase);
                List<Phrase> list;
                if (!map.PhrasesByToken.TryGetValue(tokens[0], out list))
                    map.PhrasesByToken[tokens[0]] = list = new List<Phrase>();
                list.Add(phrase);
            };
            add(f.Name, string.Empty, true);
            foreach (string synonym in f.Synonyms)
                add(synonym, string.Empty, false);
            foreach (KeyValuePair<string, List<string>> scope in f.Terms)
            {
                foreach (string term in scope.Value)
                    add(term, scope.Key, false);
            }
            foreach (string not in f.Not)
            {
                string[] tokens = Tokenize(not);
                if (tokens.Length > 0)
                    f.NotTokens.Add(tokens);
            }
            foreach (KeyValuePair<string, List<string>> dir in f.Dirs)
            {
                foreach (string alias in dir.Value)
                {
                    string[] tokens = Tokenize(alias);
                    if (tokens.Length > 0)
                        f.DirTokens.Add(new KeyValuePair<string[], string>(tokens, dir.Key));
                }
            }
            f.DirTokens.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
        }

        // ---- Classification ----

        // The class of the thing the action flies: "heli", "space" or "" (fixed wing / the game's default).
        internal static string ClassOf(Map map, string game, string plane, string title, string category)
        {
            string type;
            if (game == "DCS")
            {
                if (map.PlaneTypes.TryGetValue(plane ?? string.Empty, out type))
                {
                    if (type == "rotary_wing")
                        return "heli";
                    if (type == "spaceship")
                        return "space";
                    if (type == "ground")
                        return "ground";
                    if (type.Length > 0)
                        return string.Empty;
                }
            }
            foreach (KeyValuePair<string, List<string>> cls in map.ClassGames)
            {
                if (cls.Value.Contains(game, StringComparer.OrdinalIgnoreCase))
                    return cls.Key;
            }
            foreach (KeyValuePair<string, List<string>> cls in map.ClassPlanes)
            {
                foreach (string prefix in cls.Value)
                {
                    if ((plane ?? string.Empty).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return cls.Key;
                }
            }
            string[] context = Tokenize((category ?? string.Empty) + " " + (title ?? string.Empty));
            foreach (KeyValuePair<string, List<string[]>> cls in map.ClassWords)
            {
                foreach (string[] word in cls.Value)
                {
                    if (Find(context, word, 0) >= 0)
                        return cls.Key;
                }
            }
            return string.Empty;
        }

        private static bool ScopeApplies(string scope, string game, string cls)
        {
            if (scope.Length == 0)
                return true;
            if (scope.StartsWith("class:", StringComparison.OrdinalIgnoreCase))
                return string.Equals(scope.Substring(6), cls, StringComparison.OrdinalIgnoreCase);
            if (scope.StartsWith("game:", StringComparison.OrdinalIgnoreCase))
                scope = scope.Substring(5);
            return string.Equals(scope, game, StringComparison.OrdinalIgnoreCase) ||
                   (scope.EndsWith("*", StringComparison.Ordinal) && game.StartsWith(scope.TrimEnd('*'), StringComparison.OrdinalIgnoreCase));
        }

        // Position of the phrase in the tokens (contiguous), -1 if not there.
        internal static int Find(string[] tokens, string[] phrase, int from)
        {
            if (phrase.Length == 0)
                return -1;
            for (int i = from; i + phrase.Length <= tokens.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < phrase.Length; j++)
                {
                    if (tokens[i + j] != phrase[j])
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                    return i;
            }
            return -1;
        }

        // Words a phrase may not end with when it is matched loosely: "camera toggle" must not take "Camera jiggle toggle".
        private static readonly HashSet<string> WeakLast = new HashSet<string>(StringComparer.Ordinal)
        {
            "toggle", "mode", "switch", "button", "select", "selector", "control", "set", "view", "on", "off", "hold", "lever", "knob", "axis"
        };

        // Where the phrase's words are in the tokens: contiguous, or in order with one other word between two of them
        // ("landing taxi light" = "landing light"). The loose form needs words of three letters or more, a phrase that
        // does not end with a weak word and a gap word that is no direction. Null if not there.
        private static int[] Match(Map map, string[] tokens, string[] phrase)
        {
            int at = Find(tokens, phrase, 0);
            if (at >= 0)
            {
                int[] positions = new int[phrase.Length];
                for (int j = 0; j < phrase.Length; j++)
                    positions[j] = at + j;
                return positions;
            }
            if (phrase.Length < 2 || WeakLast.Contains(phrase[phrase.Length - 1]) || phrase.Any(p => p.Length < 2))
                return null;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i] != phrase[0])
                    continue;
                int[] positions = new int[phrase.Length];
                positions[0] = i;
                int position = i;
                int gap = 0;
                bool ok = true;
                for (int j = 1; j < phrase.Length && ok; j++)
                {
                    if (position + 1 < tokens.Length && tokens[position + 1] == phrase[j])
                        position++;
                    else if (gap == 0 && position + 2 < tokens.Length && tokens[position + 2] == phrase[j] && !map.DirectionWords.Contains(tokens[position + 1]))
                    {
                        position += 2;
                        gap++;
                    }
                    else
                        ok = false;
                    positions[j] = position;
                }
                if (ok)
                    return positions;
            }
            return null;
        }

        private sealed class Candidate
        {
            public Function Function;
            public double Score;
            public Phrase Phrase;
            public bool FromId;
        }

        // The functions an action is, by the map's rules (no catalog).
        internal static List<Assignment> Classify(Map map, string game, string cls, string[] titleTokens, string[] idTokens, bool axis)
        {
            List<Candidate> found = Candidates(map, game, cls, titleTokens, axis, null);
            if (found.Count == 0 && idTokens.Length > 0)
                found = Candidates(map, game, cls, idTokens, axis, titleTokens);
            List<Assignment> result = new List<Assignment>();
            if (found.Count == 0)
                return result;
            found.Sort((a, b) =>
            {
                int c = b.Score.CompareTo(a.Score);
                return c != 0 ? c : string.CompareOrdinal(a.Function.Id, b.Function.Id);
            });
            double top = found[0].Score;
            foreach (Candidate candidate in found)
            {
                if (result.Count >= 3)
                    break;
                double share = candidate.Score / top;
                if (result.Count > 0 && share < 0.7)
                    break;
                // The title says the direction even when only the id named the function ("Up" of "Pitch.Positive").
                string dir = DirectionOf(map, candidate.Function, titleTokens, axis);
                if (dir.Length == 0 && candidate.FromId)
                    dir = DirectionOf(map, candidate.Function, idTokens, axis);
                result.Add(new Assignment
                {
                    Function = candidate.Function,
                    Dir = dir,
                    Weight = result.Count == 0 ? (candidate.FromId ? 0.9 : 1.0) : Math.Round(Math.Min(0.8, share * 0.8), 2),
                    Source = "rule",
                    Term = candidate.Phrase.Text,
                    InTitle = !candidate.FromId && candidate.Phrase.Scope.Length == 0
                });
            }
            return result;
        }

        // title: when the tokens are the id's, the title's words still rule a function out ("Galaxy Map: Right" is no
        // strafe although its id says "translate right").
        private static List<Candidate> Candidates(Map map, string game, string cls, string[] tokens, bool axis, string[] title)
        {
            Dictionary<string, Candidate> best = new Dictionary<string, Candidate>(StringComparer.Ordinal);
            HashSet<string> firsts = new HashSet<string>(tokens, StringComparer.Ordinal);
            foreach (string first in firsts)
            {
                List<Phrase> phrases;
                if (!map.PhrasesByToken.TryGetValue(first, out phrases))
                    continue;
                foreach (Phrase phrase in phrases)
                {
                    Function f = phrase.Function;
                    if ((f.Only == "axis" && !axis) || (f.Only == "button" && axis))
                        continue;
                    if (!ScopeApplies(phrase.Scope, game, cls))
                        continue;
                    int[] positions = Match(map, tokens, phrase.Tokens);
                    if (positions == null)
                        continue;
                    if (Vetoed(f, tokens, positions) || (title != null && Vetoed(f, title, new int[0])))
                        continue;
                    int gaps = positions[positions.Length - 1] - positions[0] + 1 - positions.Length;
                    double score = phrase.Tokens.Length + (phrase.Scope.Length > 0 ? 0.75 : 0) + f.Priority * 0.25 - gaps * 0.6;
                    // An axis action is more likely an axis function, a button more likely a button function.
                    if (f.Kind == "axis" && axis || f.Kind == "button" && !axis)
                        score += 0.3;
                    Candidate existing;
                    if (!best.TryGetValue(f.Id, out existing) || existing.Score < score)
                        best[f.Id] = new Candidate { Function = f, Score = score, Phrase = phrase, FromId = title != null };
                }
            }
            return best.Values.ToList();
        }

        // A "not" word anywhere outside the matched words rules the function out ("Throttle Cutoff" is no throttle axis).
        private static bool Vetoed(Function f, string[] tokens, int[] matched)
        {
            foreach (string[] not in f.NotTokens)
            {
                int from = 0;
                while (true)
                {
                    int position = Find(tokens, not, from);
                    if (position < 0)
                        break;
                    bool inside = true;
                    for (int i = position; i < position + not.Length && inside; i++)
                        inside = Array.IndexOf(matched, i) >= 0;
                    if (!inside)
                        return true;
                    from = position + 1;
                }
            }
            return false;
        }

        // The direction the action goes: the function's own words ("nose up", "retract"; only those when the function
        // lists them), else the common ones. An axis only has a side ("left", "right") for functions with one per engine
        // or wheel, or the function's own axis words (TDC "horizontal"). Keyed functions take the action's key instead
        // (PTT "1", OSB "left 12", UFC key "7").
        internal static string DirectionOf(Map map, Function f, string[] tokens, bool axis)
        {
            if (f.Keyed.Length > 0)
                return KeyOf(f, tokens);
            if (axis)
            {
                if (f.AxisDirs)
                    return Scan(f.DirTokens, tokens);
                return f.Sided ? SideOf(tokens) : string.Empty;
            }
            string dir = Scan(f.DirTokens, tokens);
            if (dir.Length > 0 || f.HasDirs)
                return dir;
            return Scan(map.GlobalDirs, tokens);
        }

        // The direction words in the tokens (longest first, each word used once), earliest in the title wins; two
        // opposite ones ("Up/Down", "On/Off") or "toggle" make a toggle.
        private static string Scan(List<KeyValuePair<string[], string>> table, string[] tokens)
        {
            List<KeyValuePair<int, string>> found = new List<KeyValuePair<int, string>>();
            bool[] used = new bool[tokens.Length];
            foreach (KeyValuePair<string[], string> dir in table)
            {
                int from = 0;
                while (true)
                {
                    int position = Find(tokens, dir.Key, from);
                    if (position < 0)
                        break;
                    bool free = true;
                    for (int i = position; i < position + dir.Key.Length; i++)
                        free &= !used[i];
                    if (free)
                    {
                        for (int i = position; i < position + dir.Key.Length; i++)
                            used[i] = true;
                        found.Add(new KeyValuePair<int, string>(position, dir.Value));
                    }
                    from = position + 1;
                }
            }
            if (found.Count == 0)
                return string.Empty;
            found.Sort((a, b) => a.Key.CompareTo(b.Key));
            List<string> dirs = found.Select(d => d.Value).Distinct().ToList();
            List<string> real = dirs.Where(d => d != "toggle").ToList();
            // Two opposite directions ("Up/Down") make a toggle; "Zoom out (toggle)" still goes out.
            if (real.Count > 1 && Opposite(real[0], real[1]))
                return "toggle";
            if (real.Count == 0)
                return "toggle";
            return real[0];
        }

        private static readonly HashSet<string> KeyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "enter", "ent", "clear", "clr", "rtn", "return", "mark", "mk", "dot", "point", "decimal", "plus", "minus", "slash", "space", "spc",
            "bksp", "back", "del", "delete", "func", "fn", "menu", "mode", "list", "ident", "rcl", "recall", "wpt", "nav", "com", "idm", "zero",
            "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "sign"
        };

        private static string KeyOf(Function f, string[] tokens)
        {
            if (f.Keyed == "number")
            {
                string number = tokens.FirstOrDefault(t => t.Length > 0 && t.Length <= 3 && t.All(char.IsDigit)) ?? string.Empty;
                if (number.Length == 0)
                    return string.Empty;
                string side = SideOf(tokens);
                return side.Length > 0 ? side + " " + number : number;
            }
            // "label": the key's own label, the last single letter / digit or key name.
            for (int i = tokens.Length - 1; i >= 0; i--)
            {
                string t = tokens[i];
                if ((t.Length == 1 && char.IsLetterOrDigit(t[0])) || KeyNames.Contains(t))
                    return t;
            }
            return string.Empty;
        }

        private static readonly string[][] Opposites =
        {
            new[] { "up", "down" }, new[] { "left", "right" }, new[] { "fwd", "aft" }, new[] { "in", "out" }, new[] { "increase", "decrease" },
            new[] { "on", "off" }, new[] { "open", "close" }, new[] { "extend", "retract" }, new[] { "next", "prev" }, new[] { "cw", "ccw" },
            new[] { "noseup", "nosedown" }, new[] { "arm", "safe" }, new[] { "lock", "unlock" }
        };

        internal static bool Opposite(string a, string b)
        {
            foreach (string[] pair in Opposites)
            {
                if ((pair[0] == a && pair[1] == b) || (pair[0] == b && pair[1] == a))
                    return true;
            }
            return false;
        }

        internal static string OppositeOf(string dir)
        {
            foreach (string[] pair in Opposites)
            {
                if (pair[0] == dir)
                    return pair[1];
                if (pair[1] == dir)
                    return pair[0];
            }
            return string.Empty;
        }

        private static string SideOf(string[] tokens)
        {
            for (int i = 0; i < tokens.Length; i++)
            {
                switch (tokens[i])
                {
                    case "left":
                    case "port":
                    case "l":
                        return "left";
                    case "right":
                    case "starboard":
                    case "stbd":
                    case "r":
                        return "right";
                    case "1":
                        if (i > 0 && (tokens[i - 1] == "engine" || tokens[i - 1] == "throttle" || tokens[i - 1] == "mixture" || tokens[i - 1] == "propeller" || tokens[i - 1] == "prop" || tokens[i - 1] == "condition" || tokens[i - 1] == "brake"))
                            return "left";
                        break;
                    case "2":
                        if (i > 0 && (tokens[i - 1] == "engine" || tokens[i - 1] == "throttle" || tokens[i - 1] == "mixture" || tokens[i - 1] == "propeller" || tokens[i - 1] == "prop" || tokens[i - 1] == "condition" || tokens[i - 1] == "brake"))
                            return "right";
                        break;
                }
            }
            return string.Empty;
        }

        // Two directions fit together: the same, or an axis without a side next to a sided one ("Throttle" next to
        // "Throttle Left": a single lever also moves with the left hand).
        internal static bool DirCompatible(string a, string b, bool axis)
        {
            a = a ?? string.Empty;
            b = b ?? string.Empty;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;
            return axis && (a.Length == 0 || b.Length == 0);
        }

        // ---- Primary axes ----

        // Words the canonical axis of a function may have besides the function's own: "AXIS ELEVATOR SET" (MSFS),
        // "Throttle axis (all engines)", "Plane control: pitch", "Joystick Pitch", Star Citizen's "v pitch", MSFS "... EX1".
        private static readonly HashSet<string> PrimaryFiller = new HashSet<string>(StringComparer.Ordinal)
        {
            "axis", "set", "control", "input", "analog", "analogue", "flight", "plane", "aircraft", "joystick", "full", "abs", "absolute",
            "main", "all", "both", "common", "engine", "v", "ex"
        };

        private static readonly HashSet<string> SideWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "left", "right", "l", "r", "port", "starboard", "stbd"
        };

        // Number ranges of a title: "THROTTLE 1 AXIS (-100 to 100%)".
        private static readonly Regex RangeNote = new Regex(@"\([^()]*[0-9%][^()]*\)", RegexOptions.Compiled);

        // Is the action the canonical axis of an axis function: "Pitch", "Elevator (pitch) axis", MSFS "AXIS ELEVATOR SET" /
        // "ELEVATOR AXIS", "Throttle Left" or "THROTTLE 1 AXIS" of a sided function? Its title has nothing but the
        // function's name, synonyms or game terms, axis words and (sided functions) a side or engine number, and the
        // function is what the action mainly is. Variants have a word of their own and are none: the balloon's "AXIS
        // BURNER PITCH SET", "GYROSCOPE PITCH AXIS", "Pitch Axis (Alternate)", "Copilot pitch", MSFS "SET CYCLIC
        // LONGITUDINAL AXIS". Returns how canonical it is (0 = the function's name; later synonyms and game terms, and every
        // side or number, count up), -1 when it is no primary axis. Search ranks these first for their function.
        internal static int PrimaryRank(Entry entry, Function f)
        {
            if (entry == null || f == null || !f.AxisKind || !entry.Command.IsAxis)
                return -1;
            int rank;
            if (entry.PrimaryRanks != null && entry.PrimaryRanks.TryGetValue(f.Id, out rank))
                return rank;
            rank = ComputePrimaryRank(entry, f);
            if (entry.PrimaryRanks == null)
                entry.PrimaryRanks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            entry.PrimaryRanks[f.Id] = rank;
            return rank;
        }

        private static int ComputePrimaryRank(Entry entry, Function f)
        {
            Assignment a = entry.Of(f.Id);
            if (a == null || a.Weight < 0.9)
                return -1;
            EngineRelationSuggest.Command c = entry.Command;
            string[] tokens = Tokenize(RangeNote.Replace(CleanTitle(c.Game, c.Title), " "));
            if (tokens.Length == 0)
                return -1;
            bool[] covered = new bool[tokens.Length];
            int best = int.MaxValue;
            // Longest phrases first, each word used once; the phrase's place in the function (name, synonyms, terms) is
            // how canonical it is.
            List<KeyValuePair<int, Phrase>> phrases = f.Phrases.Select((p, i) => new KeyValuePair<int, Phrase>(i, p))
                .Where(p => !p.Value.IsKeyword && ScopeApplies(p.Value.Scope, c.Game, entry.Class))
                .OrderByDescending(p => p.Value.Tokens.Length).ThenBy(p => p.Key).ToList();
            foreach (KeyValuePair<int, Phrase> phrase in phrases)
            {
                int from = 0;
                while (true)
                {
                    int at = Find(tokens, phrase.Value.Tokens, from);
                    if (at < 0)
                        break;
                    bool free = true;
                    for (int i = at; i < at + phrase.Value.Tokens.Length; i++)
                        free &= !covered[i];
                    if (free)
                    {
                        for (int i = at; i < at + phrase.Value.Tokens.Length; i++)
                            covered[i] = true;
                        best = Math.Min(best, phrase.Key);
                    }
                    from = at + 1;
                }
            }
            if (best == int.MaxValue)
                return -1;
            // Both ends of the axis named ("Pedals: left / right pedal", "Cyclic pitch (nose up/down)"): its direction
            // words describe the axis.
            Dictionary<string, string> dirWords = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string[], string> d in f.DirTokens.Where(d => d.Key.Length == 1))
                dirWords[d.Key[0]] = d.Value;
            HashSet<string> ends = new HashSet<string>(tokens.Where(dirWords.ContainsKey).Select(t => dirWords[t]), StringComparer.Ordinal);
            bool bothEnds = ends.Any(e => ends.Contains(OppositeOf(e)));
            int sides = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (covered[i] || PrimaryFiller.Contains(tokens[i]) || (bothEnds && dirWords.ContainsKey(tokens[i])))
                    continue;
                // MSFS "EX1": the event's version, no side.
                if (i > 0 && tokens[i - 1] == "ex" && tokens[i].All(char.IsDigit))
                    continue;
                if (f.Sided && (SideWords.Contains(tokens[i]) || (tokens[i].Length == 1 && char.IsDigit(tokens[i][0]))))
                {
                    sides++;
                    continue;
                }
                return -1;
            }
            // One lever of several comes after the one for all ("Throttle" before "Throttle Left"); the function
            // catalog's own pick (checked against the manuals) before an axis only the rules found (MSFS "Elevator Axis",
            // KEY_AXIS_ELEVATOR_SET, before the older "Elevator Set" event).
            return best + sides * 2 + (a.Source == "catalog" ? 0 : 3);
        }

        // ---- Index ----

        internal static void BuildIndex(Map map, List<EngineRelationSuggest.Command> commands)
        {
            // Aircraft types and the function catalog (slot rows) from DB/Functions.
            Dictionary<string, List<SlotLink>> slotFunctions = new Dictionary<string, List<SlotLink>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<Function>> functionsOfSlot = new Dictionary<string, List<Function>>(StringComparer.OrdinalIgnoreCase);
            foreach (Function f in map.Functions)
            {
                foreach (SlotLink link in f.Slots)
                {
                    List<Function> list;
                    if (!functionsOfSlot.TryGetValue(link.Slot, out list))
                        functionsOfSlot[link.Slot] = list = new List<Function>();
                    list.Add(f);
                }
            }
            Dictionary<string, List<string>> catalogSlots = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            EngineAutoGroupResult probe = new EngineAutoGroupResult();
            Dictionary<string, EngineAutoGroup.Slot> slots;
            Dictionary<string, EngineAutoGroup.Aircraft> aircraft;
            List<EngineAutoGroup.Row> rows;
            if (EngineAutoGroup.TryLoadCatalog(EngineNlGroup.FunctionsFolder(), probe, out slots, out aircraft, out rows))
            {
                foreach (KeyValuePair<string, EngineAutoGroup.Aircraft> plane in aircraft)
                    map.PlaneTypes[plane.Key] = plane.Value.Type ?? string.Empty;
                foreach (EngineAutoGroup.Row row in rows)
                {
                    if (string.Equals(row.Status, "rejected", StringComparison.OrdinalIgnoreCase) || string.Equals(row.Status, "substitute", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string game = EngineRelationSuggest.GameKey(row.Game);
                    string key = game + "|" + EngineRelationSuggest.NodeId(game, row.Action) + "|" + (game == "DCS" ? row.Plane : game);
                    List<string> list;
                    if (!catalogSlots.TryGetValue(key, out list))
                        catalogSlots[key] = list = new List<string>();
                    if (!list.Contains(row.Slot))
                        list.Add(row.Slot);
                }
            }

            // Rules once per distinct text: DCS repeats the same title in dozens of aircraft.
            Dictionary<string, KeyValuePair<string[], List<Assignment>>> memo = new Dictionary<string, KeyValuePair<string[], List<Assignment>>>(StringComparer.Ordinal);
            foreach (EngineRelationSuggest.Command command in commands)
            {
                string key = command.Game + "|" + command.Id + "|" + command.Plane;
                if (map.ByKey.ContainsKey(key))
                    continue;
                string cls = ClassOf(map, command.Game, command.Plane, command.Title, command.Category);
                string memoKey = command.Game + "\u0001" + cls + "\u0001" + (command.IsAxis ? "a" : "b") + "\u0001" + command.Title + "\u0001" + (command.Game == "DCS" ? string.Empty : command.Id);
                KeyValuePair<string[], List<Assignment>> ruled;
                if (!memo.TryGetValue(memoKey, out ruled))
                {
                    string[] titleTokens = Tokenize(CleanTitle(command.Game, command.Title));
                    List<Assignment> found = Classify(map, command.Game, cls, titleTokens, new string[0], command.IsAxis);
                    // The panel or seat tag is context: it decides only when the rest of the title says nothing
                    // ("AVIONICS: FCC Switch").
                    if (found.Count == 0)
                    {
                        string[] full = Tokenize(command.Title);
                        if (full.Length != titleTokens.Length)
                        {
                            found = Classify(map, command.Game, cls, full, new string[0], command.IsAxis);
                            if (found.Count > 0)
                                titleTokens = full;
                        }
                    }
                    if (found.Count == 0)
                        found = Classify(map, command.Game, cls, titleTokens, IdTokens(command.Game, command.Id), command.IsAxis);
                    ruled = new KeyValuePair<string[], List<Assignment>>(titleTokens, found);
                    memo[memoKey] = ruled;
                }
                Entry entry = new Entry { Command = command, TitleTokens = ruled.Key, Class = cls };
                // The catalog is sure when one of its slots has a single function or the title agrees with it; a slot the
                // user shares between functions ("Nozzle Angle/Mixture Control") whose functions the title does not name
                // only counts half, and the title's own reading keeps its full weight.
                bool sure = false;
                List<string> catalog;
                if (catalogSlots.TryGetValue(key, out catalog))
                {
                    foreach (string slot in catalog)
                    {
                        List<Function> owners;
                        if (!functionsOfSlot.TryGetValue(slot, out owners))
                            continue;
                        double factor = 1.0;
                        if (owners.Count > 1)
                        {
                            // The title names one of the slot's functions: the others of the same or a higher weight go,
                            // the slot's lighter "also" links stay (CMS forward is still a flare).
                            Func<Function, double> linkWeight = o => o.Slots.First(s => string.Equals(s.Slot, slot, StringComparison.OrdinalIgnoreCase)).Weight;
                            List<Function> said = owners.Where(o => ruled.Value.Any(a => a.Function == o)).ToList();
                            if (said.Count > 0)
                            {
                                double named = said.Max(linkWeight);
                                owners = owners.Where(o => said.Contains(o) || linkWeight(o) < named).ToList();
                            }
                            else
                                factor = ruled.Value.Count > 0 ? 0.5 : 1.0;
                        }
                        foreach (Function f in owners)
                        {
                            SlotLink link = f.Slots.First(s => string.Equals(s.Slot, slot, StringComparison.OrdinalIgnoreCase));
                            if ((f.Only == "axis" && !command.IsAxis) || (f.Only == "button" && command.IsAxis))
                                continue;
                            double weight = Math.Round(link.Weight * factor, 2);
                            Assignment existing = entry.Of(f.Id);
                            if (existing != null && existing.Weight >= weight)
                                continue;
                            if (existing != null)
                                entry.Functions.Remove(existing);
                            if (factor >= 1.0)
                                sure = true;
                            Assignment rule = ruled.Value.FirstOrDefault(a => a.Function == f);
                            entry.Functions.Add(new Assignment
                            {
                                Function = f,
                                // A sided axis takes its side from the title: the slot's side is where the user
                                // put a single-engine throttle, not a second engine.
                                // A button whose title says its direction keeps it ("Gear up" in a gear toggle slot).
                                Dir = command.IsAxis && f.Sided ? SideOf(ruled.Key) : command.IsAxis && !f.AxisDirs ? string.Empty :
                                    rule != null && rule.Dir.Length > 0 && rule.Dir != "toggle" && !f.AxisDirs && f.Keyed.Length == 0 ? rule.Dir : link.Dir,
                                Weight = weight,
                                Source = "catalog",
                                Term = rule != null ? rule.Term : "slot " + slot,
                                InTitle = rule != null && rule.InTitle
                            });
                        }
                    }
                }
                foreach (Assignment rule in ruled.Value)
                {
                    if (entry.Of(rule.Function.Id) != null)
                        continue;
                    // The catalog knows this action: a rule that disagrees only counts a little.
                    double weight = sure ? Math.Round(rule.Weight * 0.6, 2) : rule.Weight;
                    entry.Functions.Add(new Assignment { Function = rule.Function, Dir = rule.Dir, Weight = weight, Source = rule.Source, Term = rule.Term, InTitle = rule.InTitle });
                }
                entry.Functions.Sort((a, b) => b.Weight.CompareTo(a.Weight));
                map.ByKey[key] = entry;
                map.Entries.Add(entry);
                foreach (Assignment a in entry.Functions)
                {
                    List<Entry> list;
                    if (!map.ByFunction.TryGetValue(a.Function.Id, out list))
                        map.ByFunction[a.Function.Id] = list = new List<Entry>();
                    list.Add(entry);
                }
            }
        }

        // ---- Queries ----

        internal sealed class Resolved
        {
            public Function Function;
            public string Dir = string.Empty;
            public double Score;
            public string Term = string.Empty;
            // Query words the function phrase covered.
            public int Covered;
            // Rides along with a named function ("throttle" -> the helicopter power lever).
            public bool Related;
            // Found by what the query describes (the function documents), not by a name or synonym.
            public bool Described;
        }

        internal sealed class Understanding
        {
            public List<Resolved> Functions = new List<Resolved>();
            public string Dir = string.Empty;
            public List<string> Words = new List<string>();
            // Words neither a function nor a direction explains: a bonus when an action's title has them.
            public List<string> Extra = new List<string>();
            // The query with misspelt words corrected, when any were ("did you mean").
            public string DidYouMean = string.Empty;
        }

        // A query -> functions with direction, in two passes:
        //   1. names, synonyms and game terms as phrases ("throttle", "gear up", "tms aft", "collective"), one typo per
        //      word (two in long words);
        //   2. what the query describes ("make the plane go faster", "drop countermeasures", "talk to the tower"): BM25
        //      over the function documents (name, synonyms, terms, keywords, description) with the lexicon's neighbour
        //      words, for the words pass 1 did not explain.
        // Misspelt words are corrected against every word JoyPro knows first.
        internal static Understanding Resolve(Map map, string query)
        {
            Understanding u = new Understanding();
            string[] tokens = Tokenize(query).Where(t => !map.Stopwords.Contains(t)).ToArray();
            if (tokens.Length == 0)
                return u;
            string[] corrected = tokens.Select(t => Correct(map, t)).ToArray();
            if (!corrected.SequenceEqual(tokens))
            {
                u.DidYouMean = string.Join(" ", corrected);
                tokens = corrected;
            }
            u.Words = tokens.ToList();
            bool[] union = new bool[tokens.Length];
            List<Resolved> chosen = Phrases(map, tokens, union);

            // 2. The words pass 1 left: a direction, filler, or a description of the function.
            List<int> open = new List<int>();
            for (int i = 0; i < tokens.Length; i++)
            {
                if (union[i] || map.DirectionWords.Contains(tokens[i]) || map.QueryFiller.Contains(tokens[i]))
                    continue;
                // A word a named function's own description has only says more about it ("drop countermeasures").
                if (chosen.Any(c => DocHas(map, c.Function, tokens[i])))
                {
                    union[i] = true;
                    continue;
                }
                open.Add(i);
            }
            // Nothing named and only direction words left ("go faster"): those describe the function too.
            if (chosen.Count == 0 && open.Count == 0)
            {
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (!map.QueryFiller.Contains(tokens[i]))
                        open.Add(i);
                }
            }
            if (open.Count > 0)
            {
                bool[] explained;
                List<Resolved> described = Describe(map, open.Select(i => tokens[i]).ToArray(), chosen.Count > 0, out explained);
                for (int k = 0; k < open.Count; k++)
                    union[open[k]] |= explained[k];
                foreach (Resolved r in described.Where(d => chosen.All(c => c.Function != d.Function)))
                    chosen.Add(r);
            }

            string[] restTokens = tokens.Where((t, i) => !union[i]).ToArray();
            // The direction: from the words besides the function's, else from the function's own words ("gear up" is a
            // synonym of the gear and still says "up").
            foreach (Resolved r in chosen)
            {
                r.Dir = DirectionOf(map, r.Function, r.Function.Keyed.Length > 0 ? tokens : restTokens, false);
                if (r.Dir.Length == 0 && r.Function.Keyed.Length == 0)
                    r.Dir = DirectionOf(map, r.Function, tokens, false);
            }
            u.Dir = chosen.Select(r => r.Dir).FirstOrDefault(d => d.Length > 0) ?? string.Empty;
            HashSet<string> dirUsed = new HashSet<string>(StringComparer.Ordinal);
            foreach (Resolved r in chosen.Where(c => c.Dir.Length > 0))
            {
                foreach (KeyValuePair<string[], string> table in r.Function.DirTokens.Concat(map.GlobalDirs).Where(d => d.Value == r.Dir))
                    dirUsed.UnionWith(table.Key);
            }
            u.Extra = restTokens.Where(t => !dirUsed.Contains(t) && !map.QueryFiller.Contains(t) && !map.DirectionWords.Contains(t)).Distinct().ToList();
            u.Functions = chosen.OrderByDescending(r => r.Score).ThenBy(r => r.Function.Id, StringComparer.Ordinal).ToList();
            // Related functions ride along, ranked after the named ones ("id" or "id:dir").
            foreach (Resolved r in u.Functions.Where(x => !x.Described).ToList())
            {
                foreach (string related in r.Function.Related)
                {
                    string[] parts = related.Split(':');
                    Function f;
                    if (map.ById.TryGetValue(parts[0], out f) && u.Functions.All(x => x.Function != f))
                        u.Functions.Add(new Resolved { Function = f, Dir = parts.Length > 1 ? parts[1] : r.Dir, Score = r.Score * 0.6, Term = r.Term, Covered = r.Covered, Related = true });
                }
            }
            return u;
        }

        // Pass 1: the names, synonyms and terms in the query. union: the query words they cover.
        private static List<Resolved> Phrases(Map map, string[] tokens, bool[] union)
        {
            Dictionary<string, Resolved> best = new Dictionary<string, Resolved>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, bool[]> coverage = new Dictionary<string, bool[]>(StringComparer.OrdinalIgnoreCase);
            int[] leanIndex = Enumerable.Range(0, tokens.Length).Where(i => !map.QueryFiller.Contains(tokens[i])).ToArray();
            string[] leanTokens = leanIndex.Select(i => tokens[i]).ToArray();
            foreach (Function f in map.Functions)
            {
                IEnumerable<Phrase> phrases = f.Phrases.Concat(new[] { new Phrase { Function = f, Text = f.Id, Tokens = Tokenize(f.Id.Replace('_', ' ')) } })
                    .Concat(f.KeywordPhrases);
                foreach (Phrase phrase in phrases)
                {
                    int typos;
                    string[] words = phrase.QueryTokens.Length > 0 ? phrase.QueryTokens : phrase.Tokens;
                    int[] positions = null;
                    int at = FindFuzzy(tokens, words, out typos);
                    if (at >= 0)
                        positions = Enumerable.Range(at, words.Length).ToArray();
                    else if (leanTokens.Length < tokens.Length)
                    {
                        // Also with the filler left out: "slow the ship down" has "slow down".
                        int leanAt = FindFuzzy(leanTokens, words, out typos);
                        if (leanAt >= 0)
                            positions = Enumerable.Range(leanAt, words.Length).Select(i => leanIndex[i]).ToArray();
                    }
                    if (positions == null)
                        continue;
                    // A described phrase ("go faster", "get out") counts a little less than a name of the same length.
                    double score = (words.Length * 10 - typos * 4 + (phrase.IsName ? 2 : 0) + (phrase.Scope.Length == 0 ? 1 : 0) + Math.Min(2, f.Priority) * 0.5) *
                                   (phrase.IsKeyword ? 0.95 : 1.0);
                    Resolved existing;
                    if (best.TryGetValue(f.Id, out existing) && existing.Score >= score)
                        continue;
                    best[f.Id] = new Resolved { Function = f, Score = score, Term = phrase.Text, Covered = words.Length, Described = phrase.IsKeyword };
                    bool[] covered = new bool[tokens.Length];
                    foreach (int i in positions)
                        covered[i] = true;
                    coverage[f.Id] = covered;
                }
            }
            List<Resolved> chosen = new List<Resolved>();
            if (best.Count == 0)
                return chosen;
            // The functions that cover most of the query win; one that covers less is only kept when it covers words
            // the winners do not ("gear and flaps").
            double top = best.Values.Max(r => r.Score);
            chosen.AddRange(best.Values.Where(r => r.Score >= top - 0.01));
            foreach (Resolved r in chosen)
            {
                for (int i = 0; i < tokens.Length; i++)
                    union[i] |= coverage[r.Function.Id][i];
            }
            foreach (Resolved r in best.Values.Where(r => !chosen.Contains(r)).OrderByDescending(r => r.Score))
            {
                bool[] mine = coverage[r.Function.Id];
                bool adds = false;
                bool overlaps = false;
                for (int i = 0; i < tokens.Length; i++)
                {
                    adds |= mine[i] && !union[i];
                    overlaps |= mine[i] && union[i];
                }
                // Direction words alone ("up") or filler name no function.
                bool content = false;
                for (int i = 0; i < tokens.Length; i++)
                    content |= mine[i] && !union[i] && !map.DirectionWords.Contains(tokens[i]) && !map.QueryFiller.Contains(tokens[i]);
                if (!adds || overlaps || !content)
                    continue;
                chosen.Add(r);
                for (int i = 0; i < tokens.Length; i++)
                    union[i] |= mine[i];
            }
            return chosen;
        }

        private static bool DocHas(Map map, Function f, string word)
        {
            if (f.Doc.ContainsKey(word))
                return true;
            List<string> neighbours;
            return map.Lexicon.TryGetValue(word, out neighbours) && neighbours.Any(f.Doc.ContainsKey);
        }

        private const double Bm25K1 = 1.2;
        private const double Bm25B = 0.3;

        // Pass 2: the functions whose documents fit the words best (BM25; a word also searches its lexicon neighbours,
        // weighed less). explained: which words some chosen function's document has. withNamed: pass 1 found functions
        // already, so only a clear winner is added.
        private static List<Resolved> Describe(Map map, string[] words, bool withNamed, out bool[] explained)
        {
            explained = new bool[words.Length];
            List<Resolved> result = new List<Resolved>();
            if (map.Functions.Count == 0)
                return result;
            int n = map.Functions.Count;
            Dictionary<Function, double> scores = new Dictionary<Function, double>();
            Dictionary<Function, bool[]> hits = new Dictionary<Function, bool[]>();
            for (int w = 0; w < words.Length; w++)
            {
                List<KeyValuePair<string, double>> forms = new List<KeyValuePair<string, double>> { new KeyValuePair<string, double>(words[w], 1.0) };
                List<string> neighbours;
                if (map.Lexicon.TryGetValue(words[w], out neighbours))
                    forms.AddRange(neighbours.Where(x => x != words[w]).Select(x => new KeyValuePair<string, double>(x, 0.6)));
                foreach (Function f in map.Functions)
                {
                    double best = 0;
                    foreach (KeyValuePair<string, double> form in forms)
                    {
                        double tf;
                        int df;
                        if (!f.Doc.TryGetValue(form.Key, out tf) || !map.DocFrequency.TryGetValue(form.Key, out df))
                            continue;
                        double idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                        double part = form.Value * idf * tf * (Bm25K1 + 1) / (tf + Bm25K1 * (1 - Bm25B + Bm25B * f.DocLength / map.AverageDocLength));
                        best = Math.Max(best, part);
                    }
                    if (best <= 0)
                        continue;
                    double current;
                    scores.TryGetValue(f, out current);
                    scores[f] = current + best;
                    bool[] mine;
                    if (!hits.TryGetValue(f, out mine))
                        hits[f] = mine = new bool[words.Length];
                    mine[w] = true;
                }
            }
            if (scores.Count == 0)
                return result;
            // More of the words matched counts on top of their weight: "release flares" is flares (both words), not
            // every function that releases something.
            List<KeyValuePair<Function, double>> ranked = scores
                .Select(s => new KeyValuePair<Function, double>(s.Key, s.Value * (0.5 + 0.5 * hits[s.Key].Count(h => h) / (double)words.Length)))
                .OrderByDescending(s => s.Value).ThenBy(s => s.Key.Id, StringComparer.Ordinal).ToList();
            double top = ranked[0].Value;
            if (top < (withNamed ? 2.5 : 1.2))
                return result;
            foreach (KeyValuePair<Function, double> s in ranked.Take(withNamed ? 1 : 4))
            {
                if (s.Value < top * (withNamed ? 1.0 : 0.6))
                    break;
                bool[] mine = hits[s.Key];
                for (int w = 0; w < words.Length; w++)
                    explained[w] |= mine[w];
                result.Add(new Resolved { Function = s.Key, Score = Math.Round(8 * s.Value / top, 1), Term = string.Join(" ", words.Where((x, i) => mine[i])), Covered = mine.Count(h => h), Described = true });
            }
            return result;
        }

        // A misspelt word -> the closest word JoyPro knows (titles, ids, function documents, lexicon): one typo, two in
        // words of eight letters or more; the more common word wins. Known words, numbers, short words, filler and
        // directions stay.
        internal static string Correct(Map map, string word)
        {
            if (word.Length < 4 || word.All(char.IsDigit) || map.Vocabulary.ContainsKey(word) || map.Lexicon.ContainsKey(word) ||
                map.QueryFiller.Contains(word) || map.Stopwords.Contains(word) || map.DirectionWords.Contains(word))
                return word;
            int max = word.Length >= 8 ? 2 : 1;
            string best = word;
            int bestDistance = int.MaxValue;
            int bestCount = 0;
            foreach (KeyValuePair<string, int> known in map.Vocabulary)
            {
                string k = known.Key;
                if (k.Length < 3 || Math.Abs(k.Length - word.Length) > max || k[0] != word[0])
                    continue;
                // Only to a word that means something to the search: a function's word, a lexicon word or a common one.
                if (!map.DocFrequency.ContainsKey(k) && !map.Lexicon.ContainsKey(k) && known.Value < 5)
                    continue;
                for (int d = 1; d <= max; d++)
                {
                    if (d > bestDistance)
                        break;
                    if (!WithinDistance(word, k, d))
                        continue;
                    if (d < bestDistance || known.Value > bestCount)
                    {
                        best = k;
                        bestDistance = d;
                        bestCount = known.Value;
                    }
                    break;
                }
            }
            return best;
        }

        // Fuzzy phrase match in the query: every phrase word in order, contiguous, each exact, a word start (3+ letters,
        // query word shorter) or with one typo (5+ letters; two from 8).
        private static int FindFuzzy(string[] tokens, string[] phrase, out int typos)
        {
            typos = 0;
            if (phrase.Length == 0 || phrase.Length > tokens.Length)
                return -1;
            for (int i = 0; i + phrase.Length <= tokens.Length; i++)
            {
                int t = 0;
                bool ok = true;
                for (int j = 0; j < phrase.Length && ok; j++)
                {
                    string q = tokens[i + j];
                    string p = phrase[j];
                    if (q == p)
                        continue;
                    if (q.Length >= 5 && p.Length >= 5 && q[0] == p[0] && WithinDistance(q, p, q.Length >= 8 ? 2 : 1))
                        t++;
                    else
                        ok = false;
                }
                if (ok)
                {
                    typos = t;
                    return i;
                }
            }
            return -1;
        }

        internal static bool WithinDistance(string a, string b, int max)
        {
            if (Math.Abs(a.Length - b.Length) > max)
                return false;
            int[] previous = new int[b.Length + 1];
            int[] current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++)
                previous[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                int rowMin = current[0];
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                    // Swapped neighbours count as one typo.
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                        current[j] = Math.Min(current[j], previous[j - 1]);
                    rowMin = Math.Min(rowMin, current[j]);
                }
                if (rowMin > max)
                    return false;
                int[] swap = previous;
                previous = current;
                current = swap;
            }
            return previous[b.Length] <= max;
        }

        // Plain words for a mapping: "throttle axis", "gear up", "CMS forward".
        internal static string Describe(Function f, string dir, bool axis)
        {
            string name = f.Name;
            if (axis)
                return name + (dir.Length > 0 ? " (" + dir + ")" : string.Empty) + (name.IndexOf("axis", StringComparison.OrdinalIgnoreCase) >= 0 ? string.Empty : " axis");
            return name + (dir.Length > 0 ? " " + DirWord(dir) : string.Empty);
        }

        internal static string DirWord(string dir)
        {
            switch (dir)
            {
                case "fwd": return "forward";
                case "aft": return "aft";
                case "noseup": return "nose up";
                case "nosedown": return "nose down";
                case "prev": return "previous";
                case "cw": return "clockwise";
                case "ccw": return "counter-clockwise";
                default: return dir;
            }
        }

        // ---- JSON helpers ----

        private static object Get(Dictionary<string, object> item, string key)
        {
            object value;
            return item != null && item.TryGetValue(key, out value) ? value : null;
        }

        private static Dictionary<string, object> Dict(object value)
        {
            return value as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        private static IEnumerable<object> List(object value)
        {
            System.Collections.IEnumerable list = value as System.Collections.IEnumerable;
            if (list == null || value is string)
                yield break;
            foreach (object item in list)
                yield return item;
        }

        private static IEnumerable<string> Strings(object value)
        {
            if (value is string)
            {
                yield return (string)value;
                yield break;
            }
            foreach (object item in List(value))
            {
                string text = Convert.ToString(item, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text.Trim();
            }
        }

        private static string Text(Dictionary<string, object> item, string key)
        {
            object value = Get(item, key);
            return value == null ? string.Empty : (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Trim();
        }

        private static double Number(object value, double fallback)
        {
            if (value == null)
                return fallback;
            double number;
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : fallback;
        }
    }
}
