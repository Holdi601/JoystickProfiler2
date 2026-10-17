using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    // What a command id means in each aircraft. A DCS id is no function of its own: "a2001cdnil" is "Pitch" in the jets
    // and "Flight Control Cyclic Pitch" in the helicopters, and a clickable cockpit id like "d3002pnilunilcd3vd1vpnilvunil"
    // (device 3, command 3002) is a different switch in every module ("Hydraulic emergency release knob", "TACAN
    // function knob - CW", "Master armament switch - ON"). So a search hit, a suggestion and a relation node are
    // (id, aircraft), each aircraft with its own title and category from its own list.
    //
    // The rule (docs/relation-builder.md "Same id, different meaning"):
    //   same       the normalised title (the game's tags and prefixes removed, words lower case, plural s dropped) and the
    //              normalised category are equal;
    //   near       the same words in another order ("Pitch Trim Up" / "Trim pitch up"), the same title in another category,
    //              or at most one extra plain word ("Landing gear up/down" / "Toggle landing gear up/down"): grouped,
    //              but flagged so it can be reviewed;
    //   different  anything else, and always when the titles are opposite directions ("Trim: nose up" / "Trim: nose
    //              down"), name other numbers ("Station 1" / "Station 2") or differ in a direction word.
    // Aircraft are grouped under one row only when their meanings are same or near to the row's head (its most common
    // meaning). Builders that add many aircraft at once (Auto Group, Group by description, the setup wizard, a node
    // spec without aircraft) keep an aircraft whose meaning differs only when the function map reads its own title as
    // the same function and direction; otherwise it goes into the node switched off ("auto-excluded, meaning differs"),
    // where one tick re-includes it. Other games have one list per game: one meaning per id (the same rules hold).
    internal static class EngineIdMeanings
    {
        internal enum Kind
        {
            Same,
            Near,
            Different
        }

        internal sealed class Variant
        {
            public string Title = string.Empty;
            public string Category = string.Empty;
            public string NormTitle = string.Empty;
            public string NormCategory = string.Empty;
            // The title without its bracketed notes ("(call radio menu)").
            public string CoreTitle = string.Empty;
            public string[] Tokens = new string[0];
            public EngineRelationSuggest.Command Sample;
            public readonly List<string> Planes = new List<string>();
        }

        internal sealed class Cluster
        {
            public int Index;
            public Variant Head;
            // Every variant of the cluster with how it compares to the head (the head itself: Same).
            public readonly List<KeyValuePair<Variant, Kind>> Variants = new List<KeyValuePair<Variant, Kind>>();
            public readonly Dictionary<Variant, string> Notes = new Dictionary<Variant, string>();

            public IEnumerable<string> Planes => Variants.SelectMany(v => v.Key.Planes);

            public int Count => Variants.Sum(v => v.Key.Planes.Count);
        }

        private static readonly object Sync = new object();
        private static Dictionary<string, List<EngineRelationSuggest.Command>> byId;
        private static readonly Dictionary<string, List<Cluster>> clusterMemo = new Dictionary<string, List<Cluster>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Variant> variantMemo = new Dictionary<string, Variant>(StringComparer.Ordinal);

        private static readonly HashSet<string> Filler = new HashSet<string>(StringComparer.Ordinal)
        {
            "the", "a", "an", "of", "to", "and", "or", "for", "with", "by", "at", "is", "button", "switch", "key", "command"
        };

        // Words that turn a title into another function when they differ ("Gear" / "Gear up", "Arm" / "Safe").
        private static readonly string[][] Opposites =
        {
            new[] { "up", "down" }, new[] { "left", "right" }, new[] { "on", "off" }, new[] { "open", "close" }, new[] { "in", "out" },
            new[] { "fwd", "aft" }, new[] { "forward", "back" }, new[] { "forward", "backward" }, new[] { "increase", "decrease" },
            new[] { "inc", "dec" }, new[] { "plus", "minus" }, new[] { "cw", "ccw" }, new[] { "next", "prev" }, new[] { "next", "previous" },
            new[] { "start", "stop" }, new[] { "extend", "retract" }, new[] { "arm", "safe" }, new[] { "lock", "unlock" },
            new[] { "enable", "disable" }, new[] { "raise", "lower" }, new[] { "push", "pull" }, new[] { "high", "low" },
            new[] { "nose", "tail" }, new[] { "climb", "dive" }, new[] { "depress", "release" }
        };

        private static readonly HashSet<string> DirectionTokens = new HashSet<string>(Opposites.SelectMany(p => p), StringComparer.Ordinal)
        {
            "center", "centre", "reset", "hold", "long", "short", "auto", "manual", "emergency", "1st", "2nd"
        };

        internal static void Reset()
        {
            lock (Sync)
            {
                byId = null;
                clusterMemo.Clear();
                planeClusterMemo.Clear();
                effective.Clear();
                variantMemo.Clear();
            }
        }

        private static Dictionary<string, List<EngineRelationSuggest.Command>> Index()
        {
            lock (Sync)
            {
                if (byId != null)
                    return byId;
                Dictionary<string, List<EngineRelationSuggest.Command>> map = new Dictionary<string, List<EngineRelationSuggest.Command>>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (EngineRelationSuggest.Command command in EngineRelationSuggest.Index(new List<string>()))
                {
                    string id = EngineRelationSuggest.NodeId(command.Game, command.Id);
                    if (!seen.Add(command.Game + "|" + id + "|" + command.Plane))
                        continue;
                    List<EngineRelationSuggest.Command> list;
                    if (!map.TryGetValue(command.Game + "|" + id, out list))
                        map[command.Game + "|" + id] = list = new List<EngineRelationSuggest.Command>();
                    list.Add(command);
                }
                // Some modules list only their input names ("Input.SpitfireLFMkIX.uc_1", "29Fulcrum:Inp:..."); their
                // twin list of the same module (SpitfireLFMkIX_easy, SpitfireLFMkIXCW) names the same id ("Undercarriage
                // DOWN"): that title stands for it when meanings are compared.
                effective.Clear();
                foreach (List<EngineRelationSuggest.Command> list in map.Values)
                {
                    if (list.Count < 2)
                        continue;
                    foreach (EngineRelationSuggest.Command raw in list.Where(c => IsInputName(c.Title)))
                    {
                        EngineRelationSuggest.Command twin = list.FirstOrDefault(o => !IsInputName(o.Title) && Twins(raw.Plane, o.Plane));
                        if (twin != null)
                            effective[raw] = twin;
                    }
                }
                byId = map;
                return map;
            }
        }

        private static readonly Dictionary<EngineRelationSuggest.Command, EngineRelationSuggest.Command> effective = new Dictionary<EngineRelationSuggest.Command, EngineRelationSuggest.Command>();

        internal static bool IsInputName(string title)
        {
            string t = (title ?? string.Empty).Trim();
            return t.StartsWith("Input.", StringComparison.Ordinal) || System.Text.RegularExpressions.Regex.IsMatch(t, @"^[0-9A-Za-z]+:Inp:");
        }

        // Two lists of the same module: one name is the other with a suffix ("SpitfireLFMkIX" / "SpitfireLFMkIX_easy" /
        // "SpitfireLFMkIXCW").
        private static bool Twins(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return false;
            string shorter = a.Length <= b.Length ? a : b;
            string longer = a.Length <= b.Length ? b : a;
            return shorter.Length >= 4 && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
        }

        // Every aircraft's command with this id (DCS: one per aircraft list; other games: the game's one).
        internal static List<EngineRelationSuggest.Command> Commands(string game, string id)
        {
            string g = EngineRelationSuggest.GameKey(game ?? string.Empty);
            List<EngineRelationSuggest.Command> list;
            return Index().TryGetValue(g + "|" + EngineRelationSuggest.NodeId(g, id), out list) ? list : new List<EngineRelationSuggest.Command>();
        }

        internal static EngineRelationSuggest.Command Find(string game, string id, string plane)
        {
            string g = EngineRelationSuggest.GameKey(game ?? string.Empty);
            string p = g == "DCS" ? plane : g;
            return Commands(g, id).FirstOrDefault(c => string.Equals(c.Plane, p, StringComparison.OrdinalIgnoreCase));
        }

        internal static string NormTitle(string game, string title)
        {
            string[] tokens = EngineFunctionMap.Tokenize(EngineFunctionMap.CleanTitle(game, title ?? string.Empty)).Where(t => !Filler.Contains(t)).ToArray();
            if (tokens.Length == 0)
                tokens = EngineFunctionMap.Tokenize(title ?? string.Empty);
            return string.Join(" ", tokens);
        }

        internal static string NormCategory(string category)
        {
            return string.Join("; ", (category ?? string.Empty).Split(';', ',')
                .Select(c => string.Join(" ", EngineFunctionMap.Tokenize(c.Replace("*", string.Empty))))
                .Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal));
        }

        private static Variant VariantOf(EngineRelationSuggest.Command command)
        {
            // An input name compares as its twin list's title (see Index).
            Index();
            EngineRelationSuggest.Command twin;
            lock (Sync)
                effective.TryGetValue(command, out twin);
            EngineRelationSuggest.Command meaning = twin ?? command;
            string key = command.Game + "\u0001" + command.Title + "\u0001" + command.Category + "\u0001" + (twin != null ? twin.Title + "\u0001" + twin.Category : string.Empty);
            lock (Sync)
            {
                Variant v;
                if (!variantMemo.TryGetValue(key, out v))
                {
                    v = new Variant { Title = command.Title ?? string.Empty, Category = command.Category ?? string.Empty, Sample = command };
                    v.NormTitle = NormTitle(meaning.Game, meaning.Title);
                    v.NormCategory = NormCategory(meaning.Category);
                    v.CoreTitle = NormTitle(meaning.Game, System.Text.RegularExpressions.Regex.Replace(meaning.Title ?? string.Empty, @"\([^)]*\)", " "));
                    v.Tokens = v.NormTitle.Split(' ').Where(t => t.Length > 0).ToArray();
                    variantMemo[key] = v;
                }
                return v;
            }
        }

        // How two aircraft's commands of the same id compare (see the class comment), with a short note for near ones.
        internal static Kind Compare(EngineRelationSuggest.Command a, EngineRelationSuggest.Command b, out string note)
        {
            return Compare(VariantOf(a), VariantOf(b), out note);
        }

        private static Kind Compare(Variant a, Variant b, out string note)
        {
            note = string.Empty;
            bool sameCategory = string.Equals(a.NormCategory, b.NormCategory, StringComparison.Ordinal);
            if (string.Equals(a.NormTitle, b.NormTitle, StringComparison.Ordinal))
            {
                if (sameCategory)
                    return Kind.Same;
                note = "category differs: '" + a.Category + "' / '" + b.Category + "'";
                return Kind.Near;
            }
            string category0 = sameCategory ? string.Empty : "; category differs: '" + a.Category + "' / '" + b.Category + "'";
            // "Pilot's radio trigger RADIO" / "... RADIO (call radio menu)": the same title with a note in brackets.
            if (a.CoreTitle.Length > 0 && string.Equals(a.CoreTitle, b.CoreTitle, StringComparison.Ordinal))
            {
                note = "differs only in brackets" + category0;
                return Kind.Near;
            }
            if (InternalDataManagement.HaveOppositeDirections(a.Title, b.Title))
                return Kind.Different;
            HashSet<string> sa = new HashSet<string>(a.Tokens.Select(LightStem), StringComparer.Ordinal);
            HashSet<string> sb = new HashSet<string>(b.Tokens.Select(LightStem), StringComparer.Ordinal);
            List<string> onlyA = sa.Where(t => !sb.Contains(t)).ToList();
            List<string> onlyB = sb.Where(t => !sa.Contains(t)).ToList();
            List<string> diff = onlyA.Concat(onlyB).ToList();
            if (diff.Any(t => DirectionTokens.Contains(t)))
                return Kind.Different;
            // Other numbers ("Station 1" / "Station 2") are another command; a number only one title has is a plain word.
            bool digitsA = sa.Any(t => t.Any(char.IsDigit));
            bool digitsB = sb.Any(t => t.Any(char.IsDigit));
            if (digitsA && digitsB && diff.Any(t => t.Any(char.IsDigit)))
                return Kind.Different;
            foreach (string[] pair in Opposites)
            {
                if ((sa.Contains(pair[0]) && sb.Contains(pair[1]) && !sa.Contains(pair[1])) || (sa.Contains(pair[1]) && sb.Contains(pair[0]) && !sa.Contains(pair[0])))
                    return Kind.Different;
            }
            int common = sa.Count(sb.Contains);
            string category = sameCategory ? string.Empty : "; category differs: '" + a.Category + "' / '" + b.Category + "'";
            if (diff.Count == 0)
            {
                note = "same words, other order" + category;
                return Kind.Near;
            }
            // One plain word more or less in a title of at least two words ("Toggle landing gear up/down").
            if (diff.Count == 1 && common >= 2)
            {
                note = "title differs by '" + diff[0] + "'" + category;
                return Kind.Near;
            }
            // One title is the other with up to two words more ("Start dispensing" / "UV-26 Start Dispense").
            if ((onlyA.Count == 0 || onlyB.Count == 0) && diff.Count <= 2 && common >= 2)
            {
                note = "title has '" + string.Join(" ", diff) + "' more" + category;
                return Kind.Near;
            }
            return Kind.Different;
        }

        // "dispensing" / "dispense" / "dispensed" compare the same.
        private static string LightStem(string token)
        {
            if (token.Length > 5 && token.EndsWith("ing", StringComparison.Ordinal))
                return token.Substring(0, token.Length - 3);
            if (token.Length > 4 && token.EndsWith("ed", StringComparison.Ordinal))
                return token.Substring(0, token.Length - 2);
            if (token.Length > 4 && token.EndsWith("e", StringComparison.Ordinal))
                return token.Substring(0, token.Length - 1);
            return token;
        }

        // The meanings of an id over these commands: the most common meaning first, every variant that is same or near
        // to it joins it, the rest form their own clusters the same way.
        internal static List<Cluster> Clusters(IEnumerable<EngineRelationSuggest.Command> commands)
        {
            Dictionary<Variant, Variant> distinct = new Dictionary<Variant, Variant>();
            Dictionary<string, Variant> byNorm = new Dictionary<string, Variant>(StringComparer.Ordinal);
            List<Variant> variants = new List<Variant>();
            foreach (EngineRelationSuggest.Command command in commands)
            {
                Variant shared = VariantOf(command);
                // Equal normalised text = one variant (raw spelling differences like "PITCH" / "Pitch" vanish).
                string key = shared.NormTitle + "\u0001" + shared.NormCategory;
                Variant v;
                if (!byNorm.TryGetValue(key, out v))
                {
                    v = new Variant { Title = shared.Title, Category = shared.Category, NormTitle = shared.NormTitle, NormCategory = shared.NormCategory, CoreTitle = shared.CoreTitle, Tokens = shared.Tokens, Sample = command };
                    byNorm[key] = v;
                    variants.Add(v);
                }
                if (!v.Planes.Contains(command.Plane, StringComparer.OrdinalIgnoreCase))
                    v.Planes.Add(command.Plane);
            }
            foreach (Variant v in variants)
                v.Planes.Sort(StringComparer.OrdinalIgnoreCase);
            List<Cluster> clusters = new List<Cluster>();
            foreach (Variant v in variants.OrderByDescending(x => x.Planes.Count).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Category, StringComparer.OrdinalIgnoreCase))
            {
                bool placed = false;
                foreach (Cluster c in clusters)
                {
                    string note;
                    Kind kind = Compare(c.Head, v, out note);
                    if (kind == Kind.Different)
                        continue;
                    c.Variants.Add(new KeyValuePair<Variant, Kind>(v, kind));
                    if (note.Length > 0)
                        c.Notes[v] = note;
                    placed = true;
                    break;
                }
                if (placed)
                    continue;
                Cluster cluster = new Cluster { Index = clusters.Count, Head = v };
                cluster.Variants.Add(new KeyValuePair<Variant, Kind>(v, Kind.Same));
                clusters.Add(cluster);
            }
            return clusters;
        }

        // The meanings of an id over every aircraft that has it (cached per run).
        internal static List<Cluster> Clusters(string game, string id)
        {
            string g = EngineRelationSuggest.GameKey(game ?? string.Empty);
            string key = g + "|" + EngineRelationSuggest.NodeId(g, id);
            lock (Sync)
            {
                List<Cluster> cached;
                if (clusterMemo.TryGetValue(key, out cached))
                    return cached;
            }
            List<Cluster> clusters = Clusters(Commands(g, id));
            lock (Sync)
                clusterMemo[key] = clusters;
            return clusters;
        }

        private static readonly Dictionary<string, Dictionary<string, int>> planeClusterMemo = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        // The cluster (over every aircraft) this aircraft's meaning of the id belongs to; -1 when it has no such id.
        internal static int ClusterIndex(string game, string id, string plane)
        {
            string g = EngineRelationSuggest.GameKey(game ?? string.Empty);
            if (g != "DCS")
                return 0;
            string key = g + "|" + EngineRelationSuggest.NodeId(g, id);
            Dictionary<string, int> planes;
            lock (Sync)
                planeClusterMemo.TryGetValue(key, out planes);
            if (planes == null)
            {
                planes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (Cluster c in Clusters(g, id))
                {
                    foreach (string p in c.Planes)
                        planes[p] = c.Index;
                }
                lock (Sync)
                    planeClusterMemo[key] = planes;
            }
            int index;
            return planes.TryGetValue(plane ?? string.Empty, out index) ? index : -1;
        }

        internal static Cluster ClusterOf(string game, string id, string plane)
        {
            return Clusters(game, id).FirstOrDefault(c => c.Planes.Contains(plane, StringComparer.OrdinalIgnoreCase));
        }

        // The function map reads both aircraft's commands as the same function in the same direction: by their own
        // titles, or by the function catalog (checked per aircraft). A catalog function whose aircraft's own title names
        // another function does not count (a catalog row that is wrong for it, CatalogAgainstTitle).
        internal static bool SameFunction(string game, string id, string reference, string plane)
        {
            EngineFunctionMap.Map map = EngineFunctionMap.Load(null);
            string g = EngineRelationSuggest.GameKey(game ?? string.Empty);
            string nid = EngineRelationSuggest.NodeId(g, id);
            EngineFunctionMap.Entry a;
            EngineFunctionMap.Entry b;
            if (!map.ByKey.TryGetValue(g + "|" + nid + "|" + reference, out a) || !map.ByKey.TryGetValue(g + "|" + nid + "|" + plane, out b))
                return false;
            // The title's own reading, or the function catalog's (checked per aircraft) unless the title names another
            // function.
            Func<EngineFunctionMap.Entry, EngineFunctionMap.Assignment, bool> counts = (entry, f) => f.Weight >= 0.5 &&
                (!(f.Term ?? string.Empty).StartsWith("slot ", StringComparison.Ordinal) || !CatalogAgainstTitle(entry, f));
            foreach (EngineFunctionMap.Assignment x in a.Functions.Where(f => counts(a, f)))
            {
                foreach (EngineFunctionMap.Assignment y in b.Functions.Where(f => counts(b, f)))
                {
                    if (x.Function == y.Function && string.Equals(x.Dir ?? string.Empty, y.Dir ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        // A function the catalog gives this aircraft's command only by its slot, while the aircraft's own title names
        // another function, for an id that means different things in different aircraft: the catalog row is for another
        // aircraft's meaning of the id (e.g. "Pitch" filed for an id whose title here is "Collective"). Resolve, the
        // assistant and the suggestions leave such a reading out.
        internal static bool CatalogAgainstTitle(EngineFunctionMap.Entry entry, EngineFunctionMap.Assignment a)
        {
            if (entry == null || a == null || a.Source != "catalog" || !(a.Term ?? string.Empty).StartsWith("slot ", StringComparison.Ordinal))
                return false;
            // DCS ids are never read as words: a rule's reading is the title's (also a class term like a helicopter's
            // "collective").
            if (!entry.Functions.Any(x => x.Source == "rule" && x.Function != a.Function))
                return false;
            EngineRelationSuggest.Command c = entry.Command;
            return c.Game == "DCS" && Clusters(c.Game, c.Id).Count > 1;
        }

        // For a builder that puts these aircraft into one node of the id: the aircraft whose meaning differs from the
        // majority (and that the function map does not read as the same function) are left out. Aircraft without the id
        // in their list stay (nothing to compare).
        internal static List<string> Guard(string game, string id, IEnumerable<string> planes, out List<string> excluded, out List<string> notes)
        {
            return Guard(game, id, planes, null, out excluded, out notes);
        }

        // name: the relation's name; the meaning whose title shares the most words with it is the node's (else the most
        // common one).
        internal static List<string> Guard(string game, string id, IEnumerable<string> planes, string name, out List<string> excluded, out List<string> notes)
        {
            excluded = new List<string>();
            notes = new List<string>();
            string g = EngineRelationSuggest.GameKey(game ?? string.Empty);
            List<string> wanted = planes.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (g != "DCS" || wanted.Count < 2)
                return wanted;
            HashSet<string> set = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
            List<EngineRelationSuggest.Command> commands = Commands(g, id).Where(c => set.Contains(c.Plane)).ToList();
            List<Cluster> clusters = Clusters(commands);
            if (clusters.Count < 2)
                return wanted;
            Cluster main = MainCluster(clusters, name);
            string reference = main.Head.Planes[0];
            List<string> kept = new List<string>();
            HashSet<string> drop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Cluster other in clusters.Where(c => c != main))
            {
                foreach (string plane in other.Planes)
                {
                    if (SameFunction(g, id, reference, plane))
                        continue;
                    drop.Add(plane);
                    notes.Add(plane + " " + id + ": '" + other.Head.Title + "' there, '" + main.Head.Title + "' in " + Short(main.Planes.ToList()));
                }
            }
            foreach (string plane in wanted)
            {
                if (drop.Contains(plane))
                    excluded.Add(plane);
                else
                    kept.Add(plane);
            }
            return kept;
        }

        // A node's meaning among its clusters: the one whose title shares the most words with the relation's name ("AJS
        // T1": the AJS37's "T1 Fix", not the Spitfire's radio dimmer three aircraft share), else the most common one.
        internal static Cluster MainCluster(List<Cluster> clusters, string name)
        {
            if (clusters == null || clusters.Count == 0)
                return null;
            HashSet<string> nameWords = new HashSet<string>(EngineFunctionMap.Tokenize(System.Text.RegularExpressions.Regex.Replace(name ?? string.Empty, @"\s*\[[^\]]*\]$", string.Empty))
                .Where(t => !Filler.Contains(t) && t.Length > 1), StringComparer.Ordinal);
            if (nameWords.Count == 0)
                return clusters[0];
            return clusters.OrderByDescending(c => c.Variants.Max(v => v.Key.Tokens.Count(nameWords.Contains))).ThenByDescending(c => c.Count).ThenBy(c => c.Index).First();
        }

        internal static string Short(IList<string> planes)
        {
            return planes.Count <= 3 ? string.Join(", ", planes) : string.Join(", ", planes.Take(2)) + " +" + (planes.Count - 2).ToString(CultureInfo.InvariantCulture) + " more";
        }

        // The id's meanings for a result row that holds these aircraft: the row's own variants (same / near) first, then
        // the aircraft with the same id and another meaning ("differs", not in the row).
        internal static List<EngineAircraftVariant> Describe(string game, string id, IEnumerable<string> rowPlanes, out int nearCount, out int differentCount)
        {
            nearCount = 0;
            differentCount = 0;
            List<EngineAircraftVariant> list = new List<EngineAircraftVariant>();
            string g = EngineRelationSuggest.GameKey(game ?? string.Empty);
            HashSet<string> inRow = new HashSet<string>(rowPlanes ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            List<EngineRelationSuggest.Command> all = Commands(g, id);
            if (all.Count == 0)
                return list;
            List<Cluster> rowClusters = Clusters(all.Where(c => inRow.Contains(c.Plane)));
            Variant head = rowClusters.Count > 0 ? rowClusters[0].Head : null;
            foreach (Cluster c in rowClusters)
            {
                foreach (KeyValuePair<Variant, Kind> v in c.Variants)
                {
                    string note = string.Empty;
                    Kind kind = head == null ? Kind.Same : Compare(head, v.Key, out note);
                    if (kind == Kind.Near)
                        nearCount += v.Key.Planes.Count;
                    list.Add(new EngineAircraftVariant
                    {
                        Title = v.Key.Title, Category = v.Key.Category, Match = kind == Kind.Same ? "same" : kind == Kind.Near ? "near" : "differs",
                        Note = note, Included = true, Planes = v.Key.Planes.ToList()
                    });
                    if (kind == Kind.Different)
                        differentCount += v.Key.Planes.Count;
                }
            }
            List<EngineRelationSuggest.Command> rest = all.Where(c => !inRow.Contains(c.Plane)).ToList();
            foreach (Cluster c in Clusters(rest))
            {
                foreach (KeyValuePair<Variant, Kind> v in c.Variants)
                {
                    string note = string.Empty;
                    Kind kind = head == null ? Kind.Different : Compare(head, v.Key, out note);
                    if (kind == Kind.Different)
                        differentCount += v.Key.Planes.Count;
                    list.Add(new EngineAircraftVariant
                    {
                        Title = v.Key.Title, Category = v.Key.Category, Match = kind == Kind.Same ? "same" : kind == Kind.Near ? "near" : "differs",
                        Note = note, Included = false, Planes = v.Key.Planes.ToList()
                    });
                }
            }
            return list;
        }

        // relations meanings: per game, how many ids several aircraft share, how many of them mean something else in some
        // aircraft (more than one cluster), how many only differ a little (near), and the ids with the most meanings.
        internal static EngineMeaningStats Stats(string onlyGame, int top)
        {
            EngineMeaningStats stats = new EngineMeaningStats();
            Dictionary<string, EngineMeaningGameStats> games = new Dictionary<string, EngineMeaningGameStats>(StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<string, List<Cluster>>> mixed = new List<KeyValuePair<string, List<Cluster>>>();
            foreach (KeyValuePair<string, List<EngineRelationSuggest.Command>> pair in Index())
            {
                string game = pair.Value[0].Game;
                if (!string.IsNullOrEmpty(onlyGame) && !string.Equals(game, EngineRelationSuggest.GameKey(onlyGame), StringComparison.OrdinalIgnoreCase))
                    continue;
                EngineMeaningGameStats g;
                if (!games.TryGetValue(game, out g))
                    games[game] = g = new EngineMeaningGameStats { Game = game };
                g.Ids++;
                g.Units = Math.Max(g.Units, 0);
                if (pair.Value.Count < 2)
                    continue;
                g.Shared++;
                string id = pair.Key.Substring(pair.Key.IndexOf('|') + 1);
                List<Cluster> clusters = Clusters(game, id);
                if (clusters.Any(c => c.Variants.Any(v => v.Value == Kind.Near)))
                    g.WithNear++;
                if (clusters.Count > 1)
                {
                    g.Mixed++;
                    g.AircraftLeftOut += clusters.Skip(1).Sum(c => c.Count);
                    mixed.Add(new KeyValuePair<string, List<Cluster>>(game + ":" + id, clusters));
                }
            }
            foreach (EngineMeaningGameStats g in games.Values)
            {
                HashSet<string> units = new HashSet<string>(Index().Where(p => p.Value[0].Game == g.Game).SelectMany(p => p.Value.Select(c => c.Plane)), StringComparer.OrdinalIgnoreCase);
                g.Units = units.Count;
            }
            stats.Games = games.Values.OrderByDescending(g => g.Mixed).ThenBy(g => g.Game, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (KeyValuePair<string, List<Cluster>> m in mixed.OrderByDescending(m => m.Value.Count).ThenByDescending(m => m.Value.Sum(c => c.Count)).Take(Math.Max(0, top)))
            {
                EngineMeaningExample example = new EngineMeaningExample { Id = m.Key, Meanings = m.Value.Count };
                foreach (Cluster c in m.Value.Take(6))
                    example.Variants.Add(new EngineAircraftVariant { Title = c.Head.Title, Category = c.Head.Category, Match = c.Index == 0 ? "most common" : "differs", Planes = c.Planes.Take(6).ToList() });
                stats.Examples.Add(example);
            }
            return stats;
        }

        public static string ToText(EngineMeaningStats stats)
        {
            StringBuilder sb = new StringBuilder();
            foreach (EngineMeaningGameStats g in stats.Games)
                sb.AppendLine(g.Game + ": " + g.Units.ToString(CultureInfo.InvariantCulture) + " lists, " + g.Ids.ToString(CultureInfo.InvariantCulture) + " ids, " +
                              g.Shared.ToString(CultureInfo.InvariantCulture) + " shared by several, " + g.Mixed.ToString(CultureInfo.InvariantCulture) + " mean something else in some (" +
                              g.AircraftLeftOut.ToString(CultureInfo.InvariantCulture) + " aircraft entries), " + g.WithNear.ToString(CultureInfo.InvariantCulture) + " with near variants");
            foreach (EngineMeaningExample e in stats.Examples)
            {
                sb.AppendLine("  " + e.Id + " (" + e.Meanings.ToString(CultureInfo.InvariantCulture) + " meanings)");
                foreach (EngineAircraftVariant v in e.Variants)
                    sb.AppendLine("      '" + v.Title + "'" + (v.Category.Length > 0 ? " [" + v.Category + "]" : string.Empty) + " in " + string.Join(", ", v.Planes));
            }
            return sb.ToString();
        }

        // ---- relations aircraft / check-aircraft ----

        // One node of a relation with every aircraft that has its id: on, off or not in the node, each with its own
        // title and category and how it compares to the node's meaning (the most common meaning of its switched-on
        // aircraft).
        internal static EngineNodeAircraft NodeReport(Relation relation, RelationItem node)
        {
            string game = EngineRelationSuggest.GameKey(node.Game ?? string.Empty);
            EngineNodeAircraft report = new EngineNodeAircraft { Relation = relation.NAME, Game = game, Id = node.ID ?? string.Empty };
            Dictionary<string, bool> aircraft = node.AIRCRAFT ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            List<EngineRelationSuggest.Command> all = Commands(game, node.ID);
            if (game != "DCS")
            {
                EngineRelationSuggest.Command only = all.FirstOrDefault();
                bool on = aircraft.Count == 0 || aircraft.Values.Any(v => v);
                report.Available = 1;
                report.Active = on ? 1 : 0;
                report.Title = only != null ? only.Title : node.ID;
                report.Category = only != null ? only.Category : string.Empty;
                report.Aircraft.Add(new EngineNodeAircraftEntry { Plane = game, Title = report.Title, Category = report.Category, State = on ? "on" : "off", Match = "same" });
                return report;
            }
            HashSet<string> active = new HashSet<string>(aircraft.Where(p => p.Value).Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
            List<Cluster> activeClusters = Clusters(all.Where(c => active.Contains(c.Plane)));
            Cluster main = MainCluster(activeClusters, relation.NAME);
            Variant head = main == null ? null : main.Head;
            if (head == null && all.Count > 0)
            {
                // Nothing switched on: the meaning of the node's own aircraft, else of the id's most common meaning.
                List<Cluster> own = Clusters(all.Where(c => aircraft.ContainsKey(c.Plane)));
                head = own.Count > 0 ? own[0].Head : Clusters(game, node.ID)[0].Head;
            }
            report.Title = head != null ? head.Title : node.ID;
            report.Category = head != null ? head.Category : string.Empty;
            string reference = head != null && head.Planes.Count > 0 ? head.Planes[0] : string.Empty;
            HashSet<string> listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (EngineRelationSuggest.Command c in all.OrderBy(c => c.Plane, StringComparer.OrdinalIgnoreCase))
            {
                if (!listed.Add(c.Plane))
                    continue;
                string note = string.Empty;
                Kind kind = head == null ? Kind.Same : Compare(head, VariantOf(c), out note);
                bool sameFunction = kind == Kind.Different && reference.Length > 0 && SameFunction(game, node.ID, reference, c.Plane);
                bool on;
                string state = aircraft.TryGetValue(c.Plane, out on) ? (on ? "on" : "off") : "absent";
                report.Aircraft.Add(new EngineNodeAircraftEntry
                {
                    Plane = c.Plane, Title = c.Title, Category = c.Category, State = state,
                    Match = kind == Kind.Same ? "same" : kind == Kind.Near ? "near" : "differs", Note = note, SameFunction = sameFunction
                });
            }
            // Aircraft of the node without the id in their list (an old list, a removed module).
            foreach (KeyValuePair<string, bool> plane in aircraft.Where(p => !listed.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                report.Aircraft.Add(new EngineNodeAircraftEntry { Plane = plane.Key, Title = string.Empty, State = plane.Value ? "on" : "off", Match = "unknown", Note = "the id is not in this aircraft's list" });
            report.Available = report.Aircraft.Count;
            report.Active = active.Count;
            report.Differs = report.Aircraft.Count(a => a.State == "on" && a.Match == "differs");
            report.DiffersSameFunction = report.Aircraft.Count(a => a.State == "on" && a.Match == "differs" && a.SameFunction);
            report.Near = report.Aircraft.Count(a => a.State == "on" && a.Match == "near");
            report.Excluded = report.Aircraft.Count(a => a.State == "off" && a.Match == "differs");
            report.Mixed = report.Differs > 0;
            report.Badge = report.Active.ToString(CultureInfo.InvariantCulture) + " of " + report.Available.ToString(CultureInfo.InvariantCulture) + " aircraft";
            return report;
        }

        internal static List<EngineNodeAircraft> RelationReport(Relation relation, string onlyGame, string onlyId)
        {
            List<EngineNodeAircraft> list = new List<EngineNodeAircraft>();
            foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
            {
                if (node == null || string.IsNullOrWhiteSpace(node.ID))
                    continue;
                if (!string.IsNullOrEmpty(onlyId) && !string.Equals(node.ID, onlyId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(onlyGame) && !string.Equals(EngineRelationSuggest.GameKey(node.Game ?? string.Empty), EngineRelationSuggest.GameKey(onlyGame), StringComparison.OrdinalIgnoreCase))
                    continue;
                list.Add(NodeReport(relation, node));
            }
            return list;
        }

        // relations check-aircraft: every node with switched-on aircraft whose meaning of the id differs from the node's
        // majority. Those the function map reads as the same function are listed but not proposed for dropping.
        internal static EngineAircraftCheckResult Check(string onlyRelation, bool apply, bool includeSameFunction)
        {
            EngineAircraftCheckResult result = new EngineAircraftCheckResult { Applied = apply };
            foreach (Relation relation in InternalDataManagement.AllRelations.Values.Where(r => r != null && r.NODES != null).OrderBy(r => r.NAME, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(onlyRelation) && !string.Equals(relation.NAME, onlyRelation, StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Relations++;
                foreach (RelationItem node in relation.NODES.Where(n => n != null && !string.IsNullOrWhiteSpace(n.ID)))
                {
                    result.Nodes++;
                    if (EngineRelationSuggest.GameKey(node.Game ?? string.Empty) != "DCS")
                        continue;
                    EngineNodeAircraft report = NodeReport(relation, node);
                    List<EngineNodeAircraftEntry> bad = report.Aircraft.Where(a => a.State == "on" && a.Match == "differs").ToList();
                    if (bad.Count == 0)
                        continue;
                    List<EngineNodeAircraftEntry> drop = bad.Where(a => includeSameFunction || !a.SameFunction).ToList();
                    EngineAircraftCheckNode found = new EngineAircraftCheckNode
                    {
                        Relation = relation.NAME, Game = report.Game, Id = report.Id, Title = report.Title, Category = report.Category,
                        Active = report.Active, Available = report.Available,
                        Drop = drop.Select(a => a.Plane).ToList(),
                        SameFunction = bad.Where(a => a.SameFunction && !includeSameFunction).Select(a => a.Plane).ToList()
                    };
                    foreach (IGrouping<string, EngineNodeAircraftEntry> meaning in bad.GroupBy(a => a.Title + "\u0001" + a.Category))
                    {
                        found.Meanings.Add(new EngineAircraftVariant
                        {
                            Title = meaning.First().Title, Category = meaning.First().Category, Match = "differs",
                            Note = meaning.First().SameFunction ? "the function map reads it as the same function" : string.Empty,
                            Included = true, Planes = meaning.Select(a => a.Plane).ToList()
                        });
                    }
                    result.Found.Add(found);
                    result.AircraftToDrop += found.Drop.Count;
                    result.AircraftSameFunction += found.SameFunction.Count;
                    if (apply)
                    {
                        foreach (string plane in found.Drop)
                            node.SetAircraftActivity(plane, false);
                        result.Dropped += found.Drop.Count;
                    }
                }
            }
            result.AffectedNodes = result.Found.Count(f => f.Drop.Count > 0);
            result.AffectedRelations = result.Found.Where(f => f.Drop.Count > 0).Select(f => f.Relation).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (apply)
            {
                foreach (Relation relation in InternalDataManagement.AllRelations.Values.Where(r => r != null))
                    relation.RecalculateElementCount();
            }
            return result;
        }

        // relations aircraft set: one node's selection in one step (on: switched on, also aircraft not in the node yet;
        // off: kept switched off, so suggestions do not bring them back).
        internal static int SetSelection(Relation relation, string game, string id, ICollection<string> on, ICollection<string> off, List<string> errors)
        {
            string g = EngineRelationSuggest.GameKey(game ?? string.Empty);
            RelationItem node = (relation.NODES ?? new List<RelationItem>()).FirstOrDefault(n => n != null && string.Equals(n.ID, id, StringComparison.OrdinalIgnoreCase) &&
                                                                                           string.Equals(EngineRelationSuggest.GameKey(n.Game ?? string.Empty), g, StringComparison.OrdinalIgnoreCase));
            if (node == null)
            {
                errors.Add(g + ":" + id + " is not in '" + relation.NAME + "'.");
                return 0;
            }
            if (node.AIRCRAFT == null)
                node.AIRCRAFT = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            int changed = 0;
            foreach (string plane in (on ?? new List<string>()).Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                bool before;
                if (!node.AIRCRAFT.TryGetValue(plane, out before) || !before)
                {
                    node.SetAircraftActivity(plane.Trim(), true);
                    changed++;
                }
            }
            foreach (string plane in (off ?? new List<string>()).Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                bool before;
                if (!node.AIRCRAFT.TryGetValue(plane, out before) || before)
                {
                    node.SetAircraftActivity(plane.Trim(), false);
                    changed++;
                }
            }
            relation.RecalculateElementCount();
            return changed;
        }

        public static string ToText(List<EngineNodeAircraft> nodes)
        {
            StringBuilder sb = new StringBuilder();
            foreach (EngineNodeAircraft node in nodes)
            {
                sb.AppendLine(node.Game + ":" + node.Id + " | " + node.Title + (node.Category.Length > 0 ? " [" + node.Category + "]" : string.Empty) + " | " + node.Badge +
                              (node.Mixed ? " | MIXED: " + node.Differs.ToString(CultureInfo.InvariantCulture) + " switched-on aircraft mean something else" : string.Empty) +
                              (node.Excluded > 0 ? " | " + node.Excluded.ToString(CultureInfo.InvariantCulture) + " excluded (meaning differs)" : string.Empty));
                foreach (EngineNodeAircraftEntry a in node.Aircraft)
                    sb.AppendLine("  [" + (a.State == "on" ? "x" : a.State == "off" ? "-" : " ") + "] " + a.Plane + " | " + a.Title + (a.Category.Length > 0 ? " [" + a.Category + "]" : string.Empty) +
                                  " | " + a.Match + (a.SameFunction ? " (same function)" : string.Empty) + (a.Note.Length > 0 ? " | " + a.Note : string.Empty));
            }
            return sb.ToString();
        }

        public static string ToText(EngineAircraftCheckResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Relations: " + result.Relations.ToString(CultureInfo.InvariantCulture) + ", nodes: " + result.Nodes.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Nodes with aircraft whose meaning differs: " + result.Found.Count.ToString(CultureInfo.InvariantCulture) +
                          " (" + result.AffectedNodes.ToString(CultureInfo.InvariantCulture) + " to repair in " + result.AffectedRelations.ToString(CultureInfo.InvariantCulture) + " relations)");
            sb.AppendLine("Aircraft to drop: " + result.AircraftToDrop.ToString(CultureInfo.InvariantCulture) + "; differ but the same function: " + result.AircraftSameFunction.ToString(CultureInfo.InvariantCulture));
            if (result.Applied)
                sb.AppendLine("Switched off: " + result.Dropped.ToString(CultureInfo.InvariantCulture));
            foreach (EngineAircraftCheckNode node in result.Found)
            {
                sb.AppendLine("  ! " + node.Relation + " | " + node.Game + ":" + node.Id + " | '" + node.Title + "' in " + node.Active.ToString(CultureInfo.InvariantCulture) + " of " + node.Available.ToString(CultureInfo.InvariantCulture) + " aircraft");
                foreach (EngineAircraftVariant m in node.Meanings)
                    sb.AppendLine("      '" + m.Title + "'" + (m.Category.Length > 0 ? " [" + m.Category + "]" : string.Empty) + " in " + string.Join(", ", m.Planes) + (m.Note.Length > 0 ? " (" + m.Note + ")" : string.Empty));
                if (node.Drop.Count > 0)
                    sb.AppendLine("      drop: " + string.Join("|", node.Drop));
            }
            return sb.ToString();
        }
    }

    public sealed class EngineMeaningGameStats
    {
        public string Game { get; set; }
        // Aircraft lists (DCS) or 1 (one list per game).
        public int Units { get; set; }
        public int Ids { get; set; }
        public int Shared { get; set; }
        public int Mixed { get; set; }
        public int AircraftLeftOut { get; set; }
        public int WithNear { get; set; }

        public EngineMeaningGameStats()
        {
            Game = string.Empty;
        }
    }

    public sealed class EngineMeaningExample
    {
        public string Id { get; set; }
        public int Meanings { get; set; }
        public List<EngineAircraftVariant> Variants { get; set; }

        public EngineMeaningExample()
        {
            Id = string.Empty;
            Variants = new List<EngineAircraftVariant>();
        }
    }

    public sealed class EngineMeaningStats
    {
        public List<EngineMeaningGameStats> Games { get; set; }
        public List<EngineMeaningExample> Examples { get; set; }

        public EngineMeaningStats()
        {
            Games = new List<EngineMeaningGameStats>();
            Examples = new List<EngineMeaningExample>();
        }
    }

    // One meaning of an id in a result row: the title and category of these aircraft, how it compares to the row
    // ("same", "near", "differs"), and whether these aircraft are in the row (Included) or only share the id.
    public sealed class EngineAircraftVariant
    {
        public string Title { get; set; }
        public string Category { get; set; }
        public string Match { get; set; }
        public string Note { get; set; }
        public bool Included { get; set; }
        public List<string> Planes { get; set; }

        public EngineAircraftVariant()
        {
            Title = Category = Match = Note = string.Empty;
            Planes = new List<string>();
        }
    }

    public sealed class EngineNodeAircraftEntry
    {
        public string Plane { get; set; }
        public string Title { get; set; }
        public string Category { get; set; }
        // "on" (in the node, switched on), "off" (in the node, switched off), "absent" (has the id, not in the node).
        public string State { get; set; }
        // "same", "near", "differs" (to the node's meaning), "unknown" (the id is not in this aircraft's list).
        public string Match { get; set; }
        public string Note { get; set; }
        // Differs in title, but the function map reads it as the same function and direction.
        public bool SameFunction { get; set; }

        public EngineNodeAircraftEntry()
        {
            Plane = Title = Category = State = Match = Note = string.Empty;
        }
    }

    public sealed class EngineNodeAircraft
    {
        public string Relation { get; set; }
        public string Game { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public string Category { get; set; }
        public int Active { get; set; }
        public int Available { get; set; }
        public int Differs { get; set; }
        public int DiffersSameFunction { get; set; }
        public int Near { get; set; }
        public int Excluded { get; set; }
        public bool Mixed { get; set; }
        // "82 of 84 aircraft"
        public string Badge { get; set; }
        public List<EngineNodeAircraftEntry> Aircraft { get; set; }

        public EngineNodeAircraft()
        {
            Relation = Game = Id = Title = Category = Badge = string.Empty;
            Aircraft = new List<EngineNodeAircraftEntry>();
        }
    }

    public sealed class EngineNodeAircraftResult
    {
        public string Relation { get; set; }
        public List<EngineNodeAircraft> Nodes { get; set; }
        public int Changed { get; set; }
        public List<string> Errors { get; set; }

        public EngineNodeAircraftResult()
        {
            Relation = string.Empty;
            Nodes = new List<EngineNodeAircraft>();
            Errors = new List<string>();
        }
    }

    public sealed class EngineAircraftCheckNode
    {
        public string Relation { get; set; }
        public string Game { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public string Category { get; set; }
        public int Active { get; set; }
        public int Available { get; set; }
        // The switched-on aircraft whose meaning of the id differs, grouped by their meaning.
        public List<EngineAircraftVariant> Meanings { get; set; }
        // Proposed to switch off (the repair).
        public List<string> Drop { get; set; }
        // Differ in title but the function map reads them as the same function: kept.
        public List<string> SameFunction { get; set; }

        public EngineAircraftCheckNode()
        {
            Relation = Game = Id = Title = Category = string.Empty;
            Meanings = new List<EngineAircraftVariant>();
            Drop = new List<string>();
            SameFunction = new List<string>();
        }
    }

    public sealed class EngineAircraftCheckResult
    {
        public int Relations { get; set; }
        public int Nodes { get; set; }
        public int AffectedNodes { get; set; }
        public int AffectedRelations { get; set; }
        public int AircraftToDrop { get; set; }
        public int AircraftSameFunction { get; set; }
        public bool Applied { get; set; }
        public int Dropped { get; set; }
        public List<EngineAircraftCheckNode> Found { get; set; }
        public List<string> Errors { get; set; }

        public EngineAircraftCheckResult()
        {
            Found = new List<EngineAircraftCheckNode>();
            Errors = new List<string>();
        }
    }
}
