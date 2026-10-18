using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    // "Find actions" of the relation editor: one search over the actions of every game and aircraft at once, built on
    // the function knowledge map (EngineFunctionMap). The query is read as functions with synonyms ("throttle" =
    // "thrust" = "collective" = Star Citizen's "strafe longitudinal") and a direction ("gear up"); the rest of the
    // words must be in the title. Ranked in tiers: the exact title, then the function by its own words, then the
    // function by a synonym or game term, then plain word matches (whole words, word starts, one typo per word, the
    // readable id and the game's category). One row per game command and title; DCS rows carry every aircraft that has
    // it. Rows are grouped by game (Groups) and every game with a function hit keeps its best rows within the limit.
    internal static class EngineRelationSearch
    {
        private const int ExactScore = 1000;
        private const int FunctionScore = 600;
        private const int WordScore = 300;
        // The function's canonical axis (EngineFunctionMap.PrimaryRank: MSFS "AXIS ELEVATOR SET" for pitch) before its
        // variants (cyclic, gyroscope, balloon burner, alternate axes), also before a title that has the query's word.
        private const int PrimaryScore = 450;
        private const int PrimaryTieScore = 60;

        private static readonly HashSet<string> Filler = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "to", "and", "or", "for", "on", "in", "with", "by", "at", "is"
        };

        private sealed class Row
        {
            public EngineRelationSuggest.Command First;
            public EngineFunctionMap.Entry Entry;
            public double Score;
            public string Why = string.Empty;
            public string Match = string.Empty;
            public string Function = string.Empty;
            public string Dir = string.Empty;
            public string Term = string.Empty;
            public double Weight;
            // The function's main control in its game (EngineFunctionMap.PrimaryRank), asked for by the function's own
            // words: MSFS "AXIS ELEVATOR SET" for "pitch".
            public bool Primary;
            // The meaning of the id this row stands for (EngineIdMeanings: DCS aircraft whose title and category of the
            // id are the same or near); other games: 0.
            public int Cluster;
            public readonly SortedSet<string> Planes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public static EngineRelationSearchResult Search(string query, ICollection<string> games, ICollection<string> planes, bool? axis, int limit)
        {
            return Search(query, games, planes, axis, limit, string.Empty);
        }

        // category: only commands of this category of the game's list (DCS: the DB's category; Star Citizen: the action
        // map); with it the words may be empty: the whole category, by title ("pick from a game's list").
        public static EngineRelationSearchResult Search(string query, ICollection<string> games, ICollection<string> planes, bool? axis, int limit, string category)
        {
            return Search(query, games, planes, axis, limit, category, 0);
        }

        // perGame: at most this many rows per game (0 = no cap), before the overall limit.
        public static EngineRelationSearchResult Search(string query, ICollection<string> games, ICollection<string> planes, bool? axis, int limit, string category, int perGame)
        {
            return Search(query, games, planes, axis, limit, category, perGame, false);
        }

        // wholeList: no words and no category are needed: every action of the games and aircraft asked for (Browse).
        private static EngineRelationSearchResult Search(string query, ICollection<string> games, ICollection<string> planes, bool? axis, int limit, string category, int perGame, bool wholeList)
        {
            EngineRelationSearchResult result = new EngineRelationSearchResult { Query = (query ?? string.Empty).Trim(), Category = (category ?? string.Empty).Trim() };
            EngineFunctionMap.Map map = EngineFunctionMap.Load(result.Warnings);
            result.LoadMilliseconds = map.LoadMilliseconds;
            Stopwatch watch = Stopwatch.StartNew();
            string[] words = EngineFunctionMap.Tokenize(result.Query).Where(t => !Filler.Contains(t)).Distinct().ToArray();
            if (words.Length == 0 && result.Category.Length == 0 && !wholeList)
            {
                result.Errors.Add("Type what the action does, e.g. \"gear up\", \"flare\", \"CMS aft\".");
                return result;
            }
            HashSet<string> gameFilter = new HashSet<string>((games ?? new List<string>()).Select(EngineRelationSuggest.GameKey).Where(g => g.Length > 0), StringComparer.OrdinalIgnoreCase);
            HashSet<string> planeFilter = new HashSet<string>((planes ?? new List<string>()).Select(p => p.Trim()).Where(p => p.Length > 0), StringComparer.OrdinalIgnoreCase);
            string categoryFilter = result.Category;
            Func<EngineRelationSuggest.Command, bool> wanted = c =>
                (gameFilter.Count == 0 || gameFilter.Contains(c.Game)) &&
                (planeFilter.Count == 0 || planeFilter.Contains(c.Plane)) &&
                (!axis.HasValue || c.IsAxis == axis.Value) &&
                (categoryFilter.Length == 0 || string.Equals(c.Category, categoryFilter, StringComparison.OrdinalIgnoreCase));

            Dictionary<string, Row> rows = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            if (words.Length == 0)
            {
                // A whole category, by title.
                foreach (EngineFunctionMap.Entry entry in map.Entries)
                {
                    if (wanted(entry.Command))
                        Add(rows, entry, 50, string.Empty, "category", null);
                }
            }
            else
            {
                EngineFunctionMap.Understanding u = EngineFunctionMap.Resolve(map, result.Query);
                if (u.DidYouMean.Length > 0)
                {
                    result.DidYouMean = u.DidYouMean;
                    words = u.DidYouMean.Split(' ').Where(t => t.Length > 0 && !Filler.Contains(t)).Distinct().ToArray();
                }
                foreach (EngineFunctionMap.Resolved r in u.Functions)
                {
                    result.Functions.Add(r.Function.Name);
                    result.FunctionIds.Add(r.Function.Id);
                    result.Resolved.Add(new EngineSearchFunction { Id = r.Function.Id, Name = r.Function.Name, Category = r.Function.Category, Kind = r.Function.Kind, Dir = r.Dir, Term = r.Term, Related = r.Related, Described = r.Described });
                }
                FunctionHits(map, u, words, wanted, rows);
                string[] content = words.Where(w => !map.QueryFiller.Contains(w)).ToArray();
                WordHits(map, content.Length > 0 ? content : words, words, wanted, rows);
            }

            Dictionary<string, List<string>> users = Users();
            List<Row> ranked = rows.Values.OrderByDescending(r => r.Score).ThenByDescending(r => r.Planes.Count).ThenBy(r => r.First.Game, StringComparer.Ordinal)
                .ThenBy(r => r.First.Title, StringComparer.OrdinalIgnoreCase).ToList();
            List<Row> capped = ranked;
            if (perGame > 0)
            {
                Dictionary<string, int> perGameCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                capped = new List<Row>();
                foreach (Row row in ranked)
                {
                    int n;
                    perGameCount.TryGetValue(row.First.Game, out n);
                    if (n >= perGame)
                        continue;
                    perGameCount[row.First.Game] = n + 1;
                    capped.Add(row);
                }
            }
            List<Row> shown = Limit(capped, limit);
            result.More = shown.Count < ranked.Count;
            foreach (Row row in shown)
            {
                // A DCS row holds every aircraft whose meaning of the id is the row's (same or near title and category),
                // also those the words alone did not find; aircraft with another meaning of the id are not in it.
                string title = row.First.Title;
                string rowCategory = row.First.Category;
                if (row.First.Game == "DCS" && row.Cluster >= 0)
                {
                    List<EngineIdMeanings.Cluster> clusters = EngineIdMeanings.Clusters(row.First.Game, row.First.Id);
                    if (row.Cluster < clusters.Count)
                    {
                        EngineIdMeanings.Cluster cluster = clusters[row.Cluster];
                        foreach (KeyValuePair<EngineIdMeanings.Variant, EngineIdMeanings.Kind> v in cluster.Variants)
                        {
                            foreach (string plane in v.Key.Planes)
                            {
                                EngineRelationSuggest.Command member = EngineIdMeanings.Find("DCS", row.First.Id, plane);
                                if (member != null && wanted(member))
                                    row.Planes.Add(plane);
                            }
                        }
                        title = cluster.Head.Title;
                        rowCategory = cluster.Head.Category;
                    }
                }
                int near = 0;
                int different = 0;
                List<EngineAircraftVariant> variants = row.First.Game == "DCS"
                    ? EngineIdMeanings.Describe(row.First.Game, row.First.Id, row.Planes, out near, out different)
                    : new List<EngineAircraftVariant>();
                // "AXIS ELEVATOR SET - Pitch axis (elevator)": a row found as the function whose title does not name it
                // says what it is.
                string label = FunctionLabel(map, row);
                string display = label.Length > 0 && !NamesFunction(map, row, title) ? title + " \u2014 " + label : title;
                List<string> inRelations = new List<string>();
                foreach (string plane in row.Planes)
                {
                    List<string> list;
                    if (users.TryGetValue(row.First.Game + "|" + row.First.Id + "|" + plane, out list))
                        inRelations.AddRange(list);
                }
                result.Results.Add(new EngineRelationSearchHit
                {
                    Game = row.First.Game,
                    Id = row.First.Id,
                    Title = title,
                    Category = rowCategory,
                    IsAxis = row.First.IsAxis,
                    Planes = row.Planes.ToList(),
                    Variants = variants,
                    Near = near,
                    SameIdDifferent = different,
                    MeaningNote = MeaningNote(near, different),
                    Score = (int)Math.Round(row.Score),
                    Primary = row.Primary,
                    FunctionLabel = label,
                    Display = display,
                    Why = row.Why,
                    Match = row.Match,
                    Function = row.Function,
                    Dir = row.Dir,
                    Term = row.Term,
                    Functions = row.Entry.Functions.Where(x => x.Weight >= 0.5).Select(x => x.Function.Id + (x.Dir.Length > 0 ? ":" + x.Dir : string.Empty)).ToList(),
                    Weight = Math.Round(row.Weight, 2),
                    InRelations = inRelations.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList()
                });
            }
            foreach (IGrouping<string, EngineRelationSearchHit> game in result.Results.GroupBy(h => h.Game).OrderByDescending(g => g.Max(h => h.Score)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                result.Groups.Add(new EngineSearchGroup
                {
                    Game = game.Key,
                    Count = game.Count(),
                    Total = ranked.Count(r => r.First.Game == game.Key),
                    Best = game.Max(h => h.Score),
                    Functions = game.Count(h => h.Match == "exact" || h.Match == "function" || h.Match == "synonym" || h.Match == "described")
                });
            }
            foreach (IGrouping<string, EngineRelationSearchHit> function in result.Results.GroupBy(h => h.Function).OrderByDescending(g => g.Max(h => h.Score)))
            {
                EngineFunctionMap.Function f;
                result.FunctionGroups.Add(new EngineSearchFunctionGroup
                {
                    Function = function.Key,
                    Name = map.ById.TryGetValue(function.Key, out f) ? f.Name : string.Empty,
                    Count = function.Count(),
                    Games = function.Select(h => h.Game).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                });
            }
            result.Total = rows.Count;
            watch.Stop();
            result.ElapsedMilliseconds = watch.ElapsedMilliseconds;
            // After the search's own time: the explanations come from an index read once per run.
            if (!wholeList)
                Choose(map, result.Results, false);
            return result;
        }

        // relations browse ("Open catalog" on the Relations page): what exists, not what matches. Without a game every
        // game with its aircraft and how many actions each has; with a game (DCS: and one or more of its aircraft) every
        // action of that list as search rows (one per id and meaning, the aircraft's own title and category, the
        // relations that use it), and the game's own categories with their counts ("Control Stick; HOTAS" counts for
        // both). A DCS row holds the asked aircraft where the id means the same; its Variants list every other aircraft.
        public static EngineRelationBrowseResult Browse(ICollection<string> games, ICollection<string> planes, bool? axis)
        {
            EngineRelationBrowseResult result = new EngineRelationBrowseResult();
            Stopwatch watch = Stopwatch.StartNew();
            EngineFunctionMap.Map map = EngineFunctionMap.Load(result.Warnings);
            HashSet<string> gameFilter = new HashSet<string>((games ?? new List<string>()).Select(EngineRelationSuggest.GameKey).Where(g => g.Length > 0), StringComparer.OrdinalIgnoreCase);
            List<string> planeFilter = (planes ?? new List<string>()).Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Dictionary<string, List<string>> users = Users();
            foreach (IGrouping<string, EngineFunctionMap.Entry> game in map.Entries.Where(e => !axis.HasValue || e.Command.IsAxis == axis.Value)
                         .GroupBy(e => e.Command.Game, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                EngineBrowseGame row = new EngineBrowseGame { Game = game.Key };
                row.Actions = game.Select(e => e.Command.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                row.InRelations = game.Count(e => users.ContainsKey(game.Key + "|" + e.Command.Id + "|" + e.Command.Plane));
                // Games with one list per game have the game as their only "aircraft": no aircraft to pick.
                foreach (IGrouping<string, EngineFunctionMap.Entry> plane in game.GroupBy(e => e.Command.Plane, StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.Equals(plane.Key, game.Key, StringComparison.OrdinalIgnoreCase))
                        continue;
                    row.Planes.Add(new EngineBrowsePlane
                    {
                        Plane = plane.Key,
                        Actions = plane.Count(),
                        InRelations = plane.Count(e => users.ContainsKey(game.Key + "|" + e.Command.Id + "|" + e.Command.Plane))
                    });
                }
                result.Games.Add(row);
            }
            if (gameFilter.Count == 1)
            {
                string gameKey = gameFilter.First();
                result.Game = result.Games.Select(g => g.Game).FirstOrDefault(g => string.Equals(g, gameKey, StringComparison.OrdinalIgnoreCase)) ?? gameKey;
                bool perPlane = result.Games.Any(g => string.Equals(g.Game, result.Game, StringComparison.OrdinalIgnoreCase) && g.Planes.Count > 0);
                if (perPlane && planeFilter.Count == 0)
                {
                    result.Errors.Add("Pick an aircraft of " + result.Game + " (--plane).");
                }
                else
                {
                    result.Planes = planeFilter;
                    EngineRelationSearchResult rows = Search(string.Empty, gameFilter, planeFilter, axis, 0, string.Empty, 0, true);
                    result.Warnings.AddRange(rows.Warnings.Where(w => !result.Warnings.Contains(w)));
                    Dictionary<string, int> categories = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    Dictionary<string, int> used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    // A list's table header ("<th>Hash</th>") is no action.
                    rows.Results.RemoveAll(h => h.Id.StartsWith("<", StringComparison.Ordinal) || h.Title.StartsWith("<", StringComparison.Ordinal));
                    // Games without categories of their own (IL-2, MSFS ...): JoyPro's function categories ("Engine and
                    // power"), from the function map.
                    if (rows.Results.All(h => CategoryNames(h.Category)[0] == NoCategory))
                    {
                        result.CategorySource = "joypro";
                        Dictionary<string, EngineFunctionMap.Entry> entries = new Dictionary<string, EngineFunctionMap.Entry>(StringComparer.OrdinalIgnoreCase);
                        foreach (EngineFunctionMap.Entry entry in map.Entries.Where(e => gameFilter.Contains(e.Command.Game)))
                            entries[entry.Command.Id] = entry;
                        foreach (EngineRelationSearchHit hit in rows.Results)
                        {
                            EngineFunctionMap.Entry entry;
                            EngineFunctionMap.Assignment best = entries.TryGetValue(hit.Id, out entry) ? entry.Functions.OrderByDescending(a => a.Weight).FirstOrDefault() : null;
                            string name;
                            hit.Category = best != null && map.Categories.TryGetValue(best.Function.Category ?? string.Empty, out name) && !string.IsNullOrWhiteSpace(name) ? name : NoCategory;
                        }
                    }
                    foreach (EngineRelationSearchHit hit in rows.Results)
                    {
                        // The asked aircraft's own title and category of the id (the row's head is the most common meaning).
                        if (perPlane)
                        {
                            EngineRelationSuggest.Command own = planeFilter.Select(p => EngineIdMeanings.Find(hit.Game, hit.Id, p)).FirstOrDefault(c => c != null);
                            if (own != null)
                            {
                                hit.Title = own.Title;
                                hit.Category = own.Category;
                            }
                        }
                        hit.Display = hit.Title;
                        hit.Match = "category";
                        foreach (string category in CategoryNames(hit.Category))
                        {
                            int n;
                            categories.TryGetValue(category, out n);
                            categories[category] = n + 1;
                            if (hit.InRelations.Count > 0)
                            {
                                used.TryGetValue(category, out n);
                                used[category] = n + 1;
                            }
                        }
                        result.Results.Add(hit);
                    }
                    result.Results.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
                    Choose(map, result.Results, true);
                    foreach (KeyValuePair<string, int> category in categories.OrderBy(c => c.Key == NoCategory ? 1 : 0).ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        int n;
                        used.TryGetValue(category.Key, out n);
                        result.Categories.Add(new EngineBrowseCategory { Name = category.Key, Count = category.Value, InRelations = n });
                    }
                    result.Total = result.Results.Count;
                }
            }
            else if (gameFilter.Count > 1)
            {
                result.Errors.Add("Browse one game at a time (--game).");
            }
            watch.Stop();
            result.ElapsedMilliseconds = watch.ElapsedMilliseconds;
            return result;
        }

        // The recommended action per game (DCS: aircraft) and function among the rows, the others explained, and what
        // each action is in words (EngineActionChoice, EngineActionInfo). A row found as a function is grouped by it;
        // ownFunction (the catalog browser): every row by the function the map reads it as surely (weight 0.9+).
        private static void Choose(EngineFunctionMap.Map map, List<EngineRelationSearchHit> hits, bool ownFunction)
        {
            List<EngineActionChoice.Item> items = new List<EngineActionChoice.Item>();
            List<EngineRelationSearchHit> grouped = new List<EngineRelationSearchHit>();
            Dictionary<string, HashSet<string>> words = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (EngineRelationSearchHit hit in hits)
            {
                EngineFunctionMap.Entry entry = null;
                if (map != null)
                    map.ByKey.TryGetValue(hit.Game + "|" + hit.Id + "|" + (hit.Game == "DCS" ? hit.Planes.FirstOrDefault() : hit.Game), out entry);
                EngineFunctionMap.Function f = null;
                if (map != null && hit.Function.Length > 0)
                    map.ById.TryGetValue(hit.Function, out f);
                if (f == null && ownFunction && entry != null)
                {
                    EngineFunctionMap.Assignment top = entry.Functions.Where(a => a.Weight >= 0.9).OrderByDescending(a => a.Weight).FirstOrDefault();
                    f = top == null ? null : top.Function;
                }
                string readable = EngineActionInfo.Readable(hit.Game, hit.Id, hit.Title);
                hit.Readable = readable;
                // "Elevator Axis — Pitch axis (elevator)", not the event name.
                if (hit.Display.Length == 0 || hit.Display == hit.Title)
                    hit.Display = readable;
                else if (hit.Display.StartsWith(hit.Title, StringComparison.Ordinal))
                    hit.Display = readable + hit.Display.Substring(hit.Title.Length);
                hit.FunctionName = f != null ? EngineFunctionMap.Describe(f, string.Empty, hit.IsAxis) : string.Empty;
                if (f == null)
                {
                    EngineActionInfo.Info plain = EngineActionInfo.Describe(hit.Game, hit.Game == "DCS" ? hit.Planes.FirstOrDefault() : hit.Game, hit.Id, hit.Title, string.Empty, string.Empty);
                    hit.Explanation = plain.Explanation;
                    hit.ExplanationSource = plain.Source;
                    continue;
                }
                HashSet<string> known;
                if (!words.TryGetValue(f.Id, out known))
                    words[f.Id] = known = EngineActionChoice.WordsOf(f);
                items.Add(new EngineActionChoice.Item
                {
                    Game = hit.Game, Id = hit.Id, Title = readable, Category = hit.Category, Planes = hit.Planes.ToList(), Function = f.Id + "|" + (hit.IsAxis ? "a" : "b"),
                    Known = known, Score = hit.Score, PrimaryRank = entry != null ? EngineFunctionMap.PrimaryRank(entry, f) : (hit.Primary ? 0 : -1)
                });
                grouped.Add(hit);
            }
            EngineActionChoice.Mark(items);
            for (int i = 0; i < grouped.Count; i++)
            {
                EngineRelationSearchHit hit = grouped[i];
                EngineActionChoice.Item item = items[i];
                hit.Recommended = item.Recommended;
                hit.RecommendedFor = item.RecommendedFor;
                hit.Variant = item.Variant;
                hit.Group = item.Group;
                hit.Alternatives = item.Alternatives;
                string functionId = item.Function.Substring(0, item.Function.IndexOf('|'));
                EngineActionInfo.Info info = EngineActionInfo.Describe(hit.Game, hit.Game == "DCS" ? hit.Planes.FirstOrDefault() : hit.Game, hit.Id, hit.Title, functionId, hit.FunctionName, item.FitsFunction);
                hit.Explanation = info.Explanation;
                hit.ExplanationSource = info.Source;
            }
        }

        internal const string NoCategory = "Other";

        // A game's category of an action as the names it is listed under: DCS "Control Stick; HOTAS" is in both.
        internal static List<string> CategoryNames(string category)
        {
            List<string> names = (category ?? string.Empty).Split(';').Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 0)
                names.Add(NoCategory);
            return names;
        }

        // Every action of the functions the query names, with the direction it asks for.
        private static void FunctionHits(EngineFunctionMap.Map map, EngineFunctionMap.Understanding u, string[] words, Func<EngineRelationSuggest.Command, bool> wanted, Dictionary<string, Row> rows)
        {
            HashSet<string> dirWords = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string[], string> d in map.GlobalDirs)
                dirWords.UnionWith(d.Key);
            // The words the title must have for a "function" match (its own words); direction words are left out.
            string[] core = words.Where(w => !dirWords.Contains(w) && !map.Stopwords.Contains(w)).ToArray();
            // Canonical axes rank first only for a query about one function ("pitch"), not "cut the throttle".
            bool single = u.Functions.Count(x => !x.Related) == 1;
            foreach (EngineFunctionMap.Resolved r in u.Functions)
            {
                List<EngineFunctionMap.Entry> entries;
                if (!map.ByFunction.TryGetValue(r.Function.Id, out entries))
                    continue;
                bool mainTerm = r.Described || EngineFunctionMap.Tokenize(r.Term).Intersect(EngineFunctionMap.Tokenize(r.Function.Name)).Any();
                foreach (EngineFunctionMap.Entry entry in entries)
                {
                    EngineRelationSuggest.Command c = entry.Command;
                    if (!wanted(c))
                        continue;
                    // Words the query has besides the function and its direction lift the titles that have them.
                    double extra = u.Extra.Count > 0 && u.Extra.All(x => HasWord(entry, x)) ? 60 : 0;
                    EngineFunctionMap.Assignment a = entry.Of(r.Function.Id);
                    if (EngineIdMeanings.CatalogAgainstTitle(entry, a))
                        continue;
                    double dirScore = 0;
                    // A query with a direction ("gear up", "strafe left") wants that direction first; a toggle, an
                    // action without one and an axis follow; the opposite direction is no hit.
                    if (r.Dir.Length > 0)
                    {
                        if (string.Equals(a.Dir, r.Dir, StringComparison.OrdinalIgnoreCase))
                            dirScore = 40;
                        else if (a.Dir == "toggle")
                            dirScore = -100;
                        else if (a.Dir.Length == 0 || c.IsAxis)
                            dirScore = -150;
                        else
                            continue;
                    }
                    bool kind = r.Function.Kind != "both" && c.IsAxis == r.Function.AxisKind && !(r.Dir.Length > 0 && c.IsAxis && !r.Function.AxisDirs && !r.Function.Sided);
                    bool own = core.Length > 0 && core.All(w => HasWord(entry, w));
                    bool exact = IsExact(entry, words);
                    // The canonical axis first when the query names the function by its main word ("pitch", "throttle")
                    // or describes it; a query by a variant's word ("collective") still has that variant first and the
                    // canonical axis only leads the other synonym rows.
                    int primary = single && !r.Related && (r.Dir.Length == 0 || string.Equals(a.Dir, r.Dir, StringComparison.OrdinalIgnoreCase))
                        ? EngineFunctionMap.PrimaryRank(entry, r.Function) : -1;
                    double primaryScore = primary < 0 ? 0 : mainTerm || own ? PrimaryScore - Math.Min(primary, 30) * 3 : PrimaryTieScore - Math.Min(primary, 30);
                    // Among canonical axes the more canonical one wins, not the title with the query's word ("Rudder"
                    // before "Flight Control Yaw (pedals)" for "yaw").
                    double score = (exact ? ExactScore : FunctionScore) + (kind ? 150 : 0) + (own && primary < 0 ? 80 : 0) + a.Weight * 100 * (r.Related ? 0.6 : 1.0) + dirScore + extra +
                                   primaryScore;
                    if (r.Related)
                        score -= 120;
                    // A function the query only describes ranks by how well it fits (its share of the best one).
                    if (r.Described && !exact)
                        score -= 80 + (8 - Math.Min(8, r.Score)) * 15;
                    string match = exact ? "exact" : r.Described && !own ? "described" : own ? "function" : "synonym";
                    // An axis only the function catalog reads as an axis function, its title without any of the
                    // function's words (a gunner seat's "Gun Up/Down" for pitch): listed after the function's own rows and the
                    // titles with the query's words, marked "catalog".
                    if (match == "synonym" && primary < 0 && r.Function.AxisKind && c.IsAxis && a.Term.StartsWith("slot ", StringComparison.Ordinal) && !TitleHasPhrase(entry, r.Function))
                    {
                        match = "catalog";
                        score = WordScore + 40 + a.Weight * 10;
                    }
                    string why = match == "catalog"
                        ? "Function catalog: '" + EngineFunctionMap.Describe(r.Function, a.Dir, c.IsAxis) + "' (the title does not say so)"
                        : "Function '" + EngineFunctionMap.Describe(r.Function, a.Dir, c.IsAxis) + "'" +
                          (match == "synonym" || match == "described" ? (a.Term.StartsWith("slot ", StringComparison.Ordinal) ? " (function catalog)" : " ('" + a.Term + "')") : string.Empty);
                    Add(rows, entry, score, why, match, a, primary >= 0 && (mainTerm || own));
                }
            }
        }

        // The title has one of the function's names, synonyms or game terms ("Pitch", "Elevator", "steer up down"), its words
        // in any order.
        internal static bool TitleHasPhrase(EngineFunctionMap.Entry entry, EngineFunctionMap.Function f)
        {
            foreach (EngineFunctionMap.Phrase phrase in f.Phrases)
            {
                if (phrase.IsKeyword || phrase.Tokens.Length == 0)
                    continue;
                // Every word of the phrase, in any order ("AXIS CYCLIC LONGITUDINAL SET" has "longitudinal cyclic").
                if (phrase.Tokens.All(t => entry.TitleTokens.Contains(t)))
                    return true;
            }
            return false;
        }

        private static bool HasWord(EngineFunctionMap.Entry entry, string word)
        {
            foreach (string t in entry.TitleTokens)
            {
                if (t == word || (word.Length >= 3 && t.StartsWith(word, StringComparison.Ordinal)))
                    return true;
            }
            return false;
        }

        private static bool IsExact(EngineFunctionMap.Entry entry, string[] words)
        {
            string[] title = entry.TitleTokens.Where(t => !Filler.Contains(t)).ToArray();
            return title.Length == words.Length && title.SequenceEqual(words, StringComparer.Ordinal);
        }

        private static WordIndex wordIndex;

        // token -> entries whose title (or readable id) has it, built once per run.
        private sealed class WordIndex
        {
            public readonly Dictionary<string, List<int>> Title = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            public readonly Dictionary<string, List<int>> Id = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            public readonly Dictionary<string, List<int>> Category = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            public string[] Vocabulary = new string[0];
            public int Documents;
        }

        private static WordIndex Words(EngineFunctionMap.Map map)
        {
            if (wordIndex != null)
                return wordIndex;
            WordIndex index = new WordIndex { Documents = Math.Max(1, map.Entries.Count) };
            Dictionary<string, string[]> idMemo = new Dictionary<string, string[]>(StringComparer.Ordinal);
            Dictionary<string, string[]> categoryMemo = new Dictionary<string, string[]>(StringComparer.Ordinal);
            for (int i = 0; i < map.Entries.Count; i++)
            {
                EngineFunctionMap.Entry entry = map.Entries[i];
                foreach (string t in entry.TitleTokens.Distinct())
                    AddTo(index.Title, t, i);
                if (entry.Command.Game != "DCS")
                {
                    string[] ids;
                    if (!idMemo.TryGetValue(entry.Command.Id, out ids))
                        idMemo[entry.Command.Id] = ids = EngineFunctionMap.IdTokens(entry.Command.Game, entry.Command.Id);
                    foreach (string t in ids.Distinct())
                        AddTo(index.Id, t, i);
                }
                string category = entry.Command.Category ?? string.Empty;
                string[] cats;
                if (!categoryMemo.TryGetValue(category, out cats))
                    categoryMemo[category] = cats = EngineFunctionMap.Tokenize(category);
                foreach (string t in cats.Distinct())
                    AddTo(index.Category, t, i);
            }
            index.Vocabulary = index.Title.Keys.Concat(index.Id.Keys).Distinct(StringComparer.Ordinal).ToArray();
            wordIndex = index;
            return index;
        }

        private static void AddTo(Dictionary<string, List<int>> map, string key, int value)
        {
            List<int> list;
            if (!map.TryGetValue(key, out list))
                map[key] = list = new List<int>();
            if (list.Count == 0 || list[list.Count - 1] != value)
                list.Add(value);
        }

        // Plain word matches over every title: whole words count most, then word starts and one typo (two in long words),
        // the readable id and the category. Every word must be found; from two words on one may be missing when the
        // found ones are the rarer ones ("gear up" still finds War Thunder's "Toggle Gear"). Rare words weigh more.
        // query: all the query's words (an exact title must be all of them, filler included).
        private static void WordHits(EngineFunctionMap.Map map, string[] words, string[] query, Func<EngineRelationSuggest.Command, bool> wanted, Dictionary<string, Row> rows)
        {
            WordIndex index = Words(map);
            Dictionary<int, double[]> found = new Dictionary<int, double[]>();
            double[] weights = new double[words.Length];
            for (int w = 0; w < words.Length; w++)
            {
                string term = words[w];
                List<int> exact;
                int df = index.Title.TryGetValue(term, out exact) ? exact.Count : 0;
                weights[w] = 0.5 + Math.Log((index.Documents + 1.0) / (df + 1.0));
                foreach (string token in index.Vocabulary)
                {
                    double value;
                    if (token == term)
                        value = 1.0;
                    else if (term.Length >= 2 && token.StartsWith(term, StringComparison.Ordinal))
                        value = 0.8;
                    else if (term.Length >= 4 && token.Length >= 4 && token[0] == term[0] && EngineFunctionMap.WithinDistance(term, token, term.Length >= 7 ? 2 : 1))
                        value = 0.6;
                    else
                        continue;
                    List<int> list;
                    if (index.Title.TryGetValue(token, out list))
                        Mark(found, list, w, words.Length, value);
                    if (index.Id.TryGetValue(token, out list))
                        Mark(found, list, w, words.Length, Math.Min(value, 0.7));
                }
                List<int> categories;
                if (term.Length >= 3 && index.Category.TryGetValue(term, out categories))
                    Mark(found, categories, w, words.Length, 0.35);
            }
            double total = weights.Sum();
            foreach (KeyValuePair<int, double[]> hit in found)
            {
                EngineFunctionMap.Entry entry = map.Entries[hit.Key];
                if (!wanted(entry.Command))
                    continue;
                double sum = 0;
                double missedWeight = 0;
                int missing = 0;
                for (int w = 0; w < words.Length; w++)
                {
                    if (hit.Value[w] <= 0)
                    {
                        missing++;
                        missedWeight += weights[w];
                    }
                    sum += hit.Value[w] * weights[w];
                }
                if (missing > (words.Length >= 2 ? 1 : 0) || sum <= 0 || missedWeight > total * 0.45)
                    continue;
                double score = 100.0 * sum / total - 20 * missing;
                int titleWords = entry.TitleTokens.Length;
                score -= Math.Min(10, Math.Max(0, titleWords - words.Length) * 1.5);
                score = Math.Max(1, score);
                bool exact = IsExact(entry, query);
                Add(rows, entry, (exact ? ExactScore : WordScore) + score * 1.5, "Title match (" + ((int)Math.Round(score)).ToString(CultureInfo.InvariantCulture) + "%)", exact ? "exact" : "words", null);
            }
        }

        private static void Mark(Dictionary<int, double[]> found, List<int> entries, int word, int words, double value)
        {
            foreach (int e in entries)
            {
                double[] values;
                if (!found.TryGetValue(e, out values))
                    found[e] = values = new double[words];
                if (values[word] < value)
                    values[word] = value;
            }
        }

        private static void Add(Dictionary<string, Row> rows, EngineFunctionMap.Entry entry, double score, string why, string match, EngineFunctionMap.Assignment a)
        {
            Add(rows, entry, score, why, match, a, false);
        }

        private static void Add(Dictionary<string, Row> rows, EngineFunctionMap.Entry entry, double score, string why, string match, EngineFunctionMap.Assignment a, bool primary)
        {
            EngineRelationSuggest.Command command = entry.Command;
            // One row per id and meaning: a DCS id that means something else in some aircraft becomes a row per meaning.
            int cluster = command.Game == "DCS" ? EngineIdMeanings.ClusterIndex(command.Game, command.Id, command.Plane) : 0;
            string key = command.Game + "|" + command.Id + "|" + (cluster >= 0 ? cluster.ToString(CultureInfo.InvariantCulture) : "t:" + command.Title);
            Row row;
            if (!rows.TryGetValue(key, out row))
                rows[key] = row = new Row { First = command, Entry = entry, Score = -1, Cluster = cluster };
            if (score > row.Score)
            {
                row.Score = score;
                row.Why = why;
                row.Match = match;
                row.Function = a == null ? string.Empty : a.Function.Id;
                row.Dir = a == null ? string.Empty : a.Dir;
                row.Term = a == null ? string.Empty : a.Term;
                row.Weight = a == null ? 0 : a.Weight;
                row.Primary = primary;
            }
            row.Planes.Add(command.Plane);
        }

        // The best rows up to the limit, but every game with a function hit keeps its best two and the best row of each
        // synonym it is found by (a search over all games must show every game's throttle and the helicopters'
        // collective, not 80 DCS rows).
        private static List<Row> Limit(List<Row> ranked, int limit)
        {
            if (limit <= 0 || ranked.Count <= limit)
                return ranked;
            List<IGrouping<string, Row>> games = ranked.Where(r => r.Match == "exact" || r.Match == "function" || r.Match == "synonym" || r.Match == "described").GroupBy(r => r.First.Game).ToList();
            // In this order until the limit: every game's best row, every game's second row, the best row of each
            // synonym a game is found by (two per game), then the rest by score.
            List<Row> order = new List<Row>();
            order.AddRange(games.Select(g => g.First()));
            order.AddRange(games.Where(g => g.Count() > 1).Select(g => g.ElementAt(1)));
            foreach (IGrouping<string, Row> game in games)
                order.AddRange(game.Where(r => r.Match == "synonym").GroupBy(r => r.Term).Take(2).Select(s => s.First()));
            order.AddRange(ranked);
            HashSet<Row> keep = new HashSet<Row>();
            foreach (Row row in order)
            {
                if (keep.Count >= limit)
                    break;
                keep.Add(row);
            }
            return ranked.Where(keep.Contains).ToList();
        }

        // The function a row was found as, in plain words: "Pitch axis (elevator)" (the synonym or game term that found
        // it in brackets when the name does not have it); empty for word matches.
        private static string FunctionLabel(EngineFunctionMap.Map map, Row row)
        {
            EngineFunctionMap.Function f;
            if (row.Function.Length == 0 || row.Match == "words" || row.Match == "category" || !map.ById.TryGetValue(row.Function, out f))
                return string.Empty;
            if (row.Match == "catalog")
                return EngineFunctionMap.Describe(f, row.Dir, row.First.IsAxis) + " (function catalog only)";
            string label = EngineFunctionMap.Describe(f, row.Dir, row.First.IsAxis);
            string term = row.Term ?? string.Empty;
            if (term.Length > 0 && !term.StartsWith("slot ", StringComparison.Ordinal))
            {
                string[] named = EngineFunctionMap.Tokenize(label);
                if (!EngineFunctionMap.Tokenize(term).All(t => named.Contains(t)))
                    label += " (" + term + ")";
            }
            return label;
        }

        // The title has a word of the function's name ("Elevator (pitch) axis" names Pitch, "AXIS ELEVATOR SET" does not).
        private static bool NamesFunction(EngineFunctionMap.Map map, Row row, string title)
        {
            EngineFunctionMap.Function f;
            if (!map.ById.TryGetValue(row.Function, out f))
                return true;
            string[] titleTokens = EngineFunctionMap.Tokenize(title);
            return EngineFunctionMap.Tokenize(f.Name).Where(t => !Filler.Contains(t) && t != "axis").Any(t => titleTokens.Contains(t));
        }

        // "2 near (title or category differs a little) · same id, different meaning in 3 aircraft (left out)".
        internal static string MeaningNote(int near, int different)
        {
            List<string> parts = new List<string>();
            if (near > 0)
                parts.Add(near.ToString(CultureInfo.InvariantCulture) + (near == 1 ? " aircraft" : " aircraft") + " near (title or category differs a little)");
            if (different > 0)
                parts.Add("same id, different meaning in " + different.ToString(CultureInfo.InvariantCulture) + (different == 1 ? " aircraft" : " aircraft") + " (left out)");
            return string.Join(" · ", parts);
        }

        internal static List<string> Terms(string query)
        {
            string spaced = Regex.Replace(query ?? string.Empty, "([a-z])([A-Z])", "$1 $2").Replace('_', ' ');
            return Regex.Split(spaced.ToLowerInvariant(), "[^a-z0-9+-]+").Where(t => t.Length > 0 && !Filler.Contains(t)).Distinct().ToList();
        }

        // game|id|plane -> the relations that use it (switched on).
        private static Dictionary<string, List<string>> Users()
        {
            Dictionary<string, List<string>> users = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (Relation relation in InternalDataManagement.AllRelations.Values.Where(r => r != null && r.NODES != null))
            {
                foreach (RelationItem node in relation.NODES.Where(n => n != null && n.AIRCRAFT != null))
                {
                    string game = EngineRelationSuggest.GameKey(node.Game ?? string.Empty);
                    foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT.Where(p => p.Value))
                    {
                        List<string> list;
                        string key = game + "|" + EngineRelationSuggest.NodeId(game, node.ID) + "|" + (game == "DCS" ? plane.Key : game);
                        if (!users.TryGetValue(key, out list))
                            users[key] = list = new List<string>();
                        if (!list.Contains(relation.NAME))
                            list.Add(relation.NAME);
                    }
                }
            }
            return users;
        }

        public static string ToText(EngineRelationSearchResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Search: " + result.Query + (result.Category.Length > 0 ? " [category " + result.Category + "]" : string.Empty));
            if (result.DidYouMean.Length > 0)
                sb.AppendLine("Did you mean: " + result.DidYouMean);
            if (result.Resolved.Count > 0)
                sb.AppendLine("Functions: " + string.Join(", ", result.Resolved.Select(f => f.Name + (f.Dir.Length > 0 ? " " + EngineFunctionMap.DirWord(f.Dir) : string.Empty) + (f.Related ? " (related)" : f.Described ? " (described)" : string.Empty))));
            sb.AppendLine("Results: " + result.Results.Count.ToString(CultureInfo.InvariantCulture) + (result.More ? " of " + result.Total.ToString(CultureInfo.InvariantCulture) : string.Empty) +
                          " in " + result.Groups.Count.ToString(CultureInfo.InvariantCulture) + " games (" + result.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)");
            foreach (EngineSearchGroup group in result.Groups)
            {
                sb.AppendLine(group.Game + " (" + group.Count.ToString(CultureInfo.InvariantCulture) + (group.Total > group.Count ? " of " + group.Total.ToString(CultureInfo.InvariantCulture) : string.Empty) + "):");
                foreach (EngineRelationSearchHit hit in result.Results.Where(h => h.Game == group.Game))
                {
                    sb.AppendLine("  - [" + hit.Score.ToString(CultureInfo.InvariantCulture) + " " + hit.Match + (hit.Primary ? " main" : string.Empty) + "] " + hit.Game + ":" + hit.Id + ":" + string.Join("|", hit.Planes) + " | " + (hit.Display.Length > 0 ? hit.Display : hit.Title) +
                                  (hit.IsAxis ? " | axis" : string.Empty) + (hit.InRelations.Count > 0 ? " | in " + string.Join(", ", hit.InRelations) : string.Empty) +
                                  (hit.Why.Length > 0 ? " | " + hit.Why : string.Empty) + (hit.MeaningNote.Length > 0 ? " | " + hit.MeaningNote : string.Empty) +
                                  (hit.Recommended ? " | recommended" + (hit.RecommendedFor.Length > 0 ? " " + hit.RecommendedFor : string.Empty) : hit.Variant.Length > 0 ? " | variant: " + hit.Variant : string.Empty));
                }
            }
            foreach (string warning in result.Warnings.Distinct().Where(w => !w.Contains("action catalog not found")).Take(10))
                sb.AppendLine("Warning: " + warning);
            foreach (string error in result.Errors)
                sb.AppendLine("Error: " + error);
            return sb.ToString();
        }
    }

    public sealed class EngineRelationSearchHit
    {
        public string Game { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public string Category { get; set; }
        public bool IsAxis { get; set; }
        public List<string> Planes { get; set; }
        public int Score { get; set; }
        public string Why { get; set; }
        // The function's main control in this game for a query by the function's own words (MSFS "AXIS ELEVATOR SET"
        // for "pitch"): it leads its game.
        public bool Primary { get; set; }
        // The function the row was found as ("Pitch axis (elevator)"); empty for word matches.
        public string FunctionLabel { get; set; }
        // The title to show: with " - <FunctionLabel>" when the title does not name the function
        // ("AXIS ELEVATOR SET - Pitch axis (elevator)").
        public string Display { get; set; }
        // "exact" (the title is the query), "function" (the query's function, by its own words), "synonym" (the
        // function by a synonym or game term), "catalog" (only the function catalog says so, the title has none of the
        // function's words: listed low), "words" (a plain word match), "category" (a whole category).
        public string Match { get; set; }
        public string Function { get; set; }
        public string Dir { get; set; }
        // The synonym or game term that found it ("collective"), or "slot <id>" for the function catalog.
        // Every function the action is (weight 0.5 or more), as "id" or "id:dir".
        public List<string> Functions { get; set; }
        public string Term { get; set; }
        public double Weight { get; set; }
        public List<string> InRelations { get; set; }
        // DCS: every meaning of the id with its aircraft: the row's own ("same" and "near", Included) and the aircraft
        // where the same id means something else ("differs", not in Planes).
        public List<EngineAircraftVariant> Variants { get; set; }
        // Aircraft of the row whose title or category differs a little from the row's (to review).
        public int Near { get; set; }
        // Aircraft with the same id and another meaning (left out of the row).
        public int SameIdDifferent { get; set; }
        public string MeaningNote { get; set; }
        // EngineActionChoice / EngineActionInfo, as on EngineRelationSuggestion (docs/relation-builder.md "Recommended
        // action"): grouped per game (DCS: aircraft) and function.
        public bool Recommended { get; set; }
        public string RecommendedFor { get; set; }
        public string Variant { get; set; }
        public string Group { get; set; }
        public int Alternatives { get; set; }
        public string Readable { get; set; }
        public string FunctionName { get; set; }
        public string Explanation { get; set; }
        public string ExplanationSource { get; set; }

        public EngineRelationSearchHit()
        {
            Game = Id = Title = Category = Why = Match = Function = Dir = Term = MeaningNote = FunctionLabel = Display = string.Empty;
            RecommendedFor = Variant = Group = Readable = FunctionName = Explanation = ExplanationSource = string.Empty;
            Functions = new List<string>();
            Planes = new List<string>();
            InRelations = new List<string>();
            Variants = new List<EngineAircraftVariant>();
        }
    }

    public sealed class EngineSearchFunction
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Kind { get; set; }
        public string Dir { get; set; }
        public string Term { get; set; }
        public bool Related { get; set; }
        public bool Described { get; set; }

        public EngineSearchFunction()
        {
            Id = Name = Category = Kind = Dir = Term = string.Empty;
        }
    }

    public sealed class EngineSearchFunctionGroup
    {
        public string Function { get; set; }
        public string Name { get; set; }
        public int Count { get; set; }
        public List<string> Games { get; set; }

        public EngineSearchFunctionGroup()
        {
            Function = Name = string.Empty;
            Games = new List<string>();
        }
    }

    public sealed class EngineSearchGroup
    {
        public string Game { get; set; }
        public int Count { get; set; }
        public int Total { get; set; }
        public int Best { get; set; }
        public int Functions { get; set; }

        public EngineSearchGroup()
        {
            Game = string.Empty;
        }
    }

    public sealed class EngineRelationSearchResult
    {
        public string Query { get; set; }
        public string Category { get; set; }
        public List<string> Functions { get; set; }
        public List<string> FunctionIds { get; set; }
        public List<EngineSearchFunction> Resolved { get; set; }
        public List<EngineRelationSearchHit> Results { get; set; }
        public List<EngineSearchGroup> Groups { get; set; }
        // The shown rows by function, then game: function id ("" for plain word matches) -> games in rank order.
        public List<EngineSearchFunctionGroup> FunctionGroups { get; set; }
        // The query with misspelt words corrected, when any were.
        public string DidYouMean { get; set; }
        public int Total { get; set; }
        public bool More { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public long LoadMilliseconds { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public EngineRelationSearchResult()
        {
            Query = Category = string.Empty;
            Functions = new List<string>();
            FunctionIds = new List<string>();
            Resolved = new List<EngineSearchFunction>();
            Results = new List<EngineRelationSearchHit>();
            Groups = new List<EngineSearchGroup>();
            FunctionGroups = new List<EngineSearchFunctionGroup>();
            DidYouMean = string.Empty;
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }

    // relations browse: every game with its aircraft; for one game (and aircraft) its actions and categories.
    public sealed class EngineRelationBrowseResult
    {
        public List<EngineBrowseGame> Games { get; set; }
        public string Game { get; set; }
        public List<string> Planes { get; set; }
        public List<EngineBrowseCategory> Categories { get; set; }
        public List<EngineRelationSearchHit> Results { get; set; }
        public int Total { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        // Where the categories come from: "game" (the game's own) or "joypro" (the function map's, for games without).
        public string CategorySource { get; set; }

        public EngineRelationBrowseResult()
        {
            Game = string.Empty;
            CategorySource = "game";
            Games = new List<EngineBrowseGame>();
            Planes = new List<string>();
            Categories = new List<EngineBrowseCategory>();
            Results = new List<EngineRelationSearchHit>();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }

    public sealed class EngineBrowseGame
    {
        public string Game { get; set; }
        // Distinct action ids of the game (DCS: over every aircraft) and the (id, aircraft) a relation uses.
        public int Actions { get; set; }
        public int InRelations { get; set; }
        // DCS: the aircraft lists; empty for games with one list.
        public List<EngineBrowsePlane> Planes { get; set; }

        public EngineBrowseGame()
        {
            Game = string.Empty;
            Planes = new List<EngineBrowsePlane>();
        }
    }

    public sealed class EngineBrowsePlane
    {
        public string Plane { get; set; }
        public int Actions { get; set; }
        public int InRelations { get; set; }

        public EngineBrowsePlane()
        {
            Plane = string.Empty;
        }
    }

    public sealed class EngineBrowseCategory
    {
        public string Name { get; set; }
        public int Count { get; set; }
        // Rows of the category that a relation uses.
        public int InRelations { get; set; }

        public EngineBrowseCategory()
        {
            Name = string.Empty;
        }
    }
}
