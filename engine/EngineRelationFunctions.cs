using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    // relations functions / relations resolve: the function knowledge map (EngineFunctionMap) as an API for the GUI and
    // the local-model layer. "functions" lists the functions with their synonyms (and how many actions of each game on
    // this PC they cover); "resolve" turns a query into function ids and the matching actions of every game.
    internal static class EngineRelationFunctions
    {
        public static EngineFunctionListResult Functions(string category, string query, bool coverage, string dumpPath)
        {
            Stopwatch watch = Stopwatch.StartNew();
            EngineFunctionListResult result = new EngineFunctionListResult();
            EngineFunctionMap.Map map = EngineFunctionMap.Load(result.Warnings);
            result.Version = map.Version;
            result.Source = map.Source;
            result.LoadMilliseconds = map.LoadMilliseconds;
            result.ReadMilliseconds = map.IndexMilliseconds;
            result.FromCache = map.FromCache;
            foreach (KeyValuePair<string, string> c in map.Categories)
                result.Categories[c.Key] = c.Value;
            HashSet<string> wanted = null;
            if (!string.IsNullOrWhiteSpace(query))
            {
                EngineFunctionMap.Understanding u = EngineFunctionMap.Resolve(map, query);
                wanted = new HashSet<string>(u.Functions.Select(r => r.Function.Id), StringComparer.OrdinalIgnoreCase);
            }
            foreach (EngineFunctionMap.Function f in map.Functions)
            {
                if (!string.IsNullOrWhiteSpace(category) && !string.Equals(f.Category, category.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (wanted != null && !wanted.Contains(f.Id))
                    continue;
                EngineFunctionInfo info = new EngineFunctionInfo
                {
                    Id = f.Id,
                    Name = f.Name,
                    Category = f.Category,
                    CategoryName = map.Categories.ContainsKey(f.Category) ? map.Categories[f.Category] : f.Category,
                    Kind = f.Kind,
                    Only = f.Only,
                    Description = f.Description,
                    Synonyms = f.Synonyms.ToList(),
                    Terms = f.Terms.ToDictionary(t => t.Key, t => t.Value.ToList()),
                    Directions = f.Dirs.Keys.ToList(),
                    Slots = f.Slots.Select(s => s.Slot + (s.Dir.Length > 0 ? ":" + s.Dir : string.Empty)).ToList(),
                    Related = f.Related.ToList()
                };
                List<EngineFunctionMap.Entry> entries;
                if (map.ByFunction.TryGetValue(f.Id, out entries))
                {
                    info.Actions = entries.Count;
                    foreach (IGrouping<string, EngineFunctionMap.Entry> game in entries.GroupBy(e => e.Command.Game))
                        info.Games[game.Key] = game.Count();
                }
                result.Functions.Add(info);
            }
            result.Totals.Functions = map.Functions.Count;
            result.Totals.Synonyms = map.Functions.Sum(f => f.Synonyms.Count + 1);
            result.Totals.Terms = map.Functions.Sum(f => f.Terms.Values.Sum(t => t.Count));
            result.Totals.Actions = map.Entries.Count;
            result.Totals.Mapped = map.Entries.Count(e => e.Functions.Count > 0);
            if (coverage)
                result.Coverage = Coverage(map);
            if (!string.IsNullOrWhiteSpace(dumpPath))
                Dump(map, dumpPath);
            watch.Stop();
            result.ElapsedMilliseconds = watch.ElapsedMilliseconds;
            return result;
        }

        private static List<EngineFunctionCoverage> Coverage(EngineFunctionMap.Map map)
        {
            List<EngineFunctionCoverage> list = new List<EngineFunctionCoverage>();
            foreach (IGrouping<string, EngineFunctionMap.Entry> game in map.Entries.GroupBy(e => e.Command.Game).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                EngineFunctionCoverage c = new EngineFunctionCoverage { Game = game.Key };
                c.Planes = game.Select(e => e.Command.Plane).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                c.Actions = game.Count();
                c.Mapped = game.Count(e => e.Functions.Count > 0);
                c.Catalog = game.Count(e => e.Functions.Any(a => a.Source == "catalog"));
                c.Rules = game.Count(e => e.Functions.Count > 0 && e.Functions.All(a => a.Source == "rule"));
                c.Percent = c.Actions == 0 ? 0 : Math.Round(100.0 * c.Mapped / c.Actions, 1);
                c.Functions = game.SelectMany(e => e.Functions.Select(a => a.Function.Id)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                c.Unmapped = game.Where(e => e.Functions.Count == 0).Select(e => e.Command.Title).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).Take(25).ToList();
                list.Add(c);
            }
            return list;
        }

        // One line per action: game, plane, id, axis/button, title, category, class, functions (id:dir@weight/source).
        private static void Dump(EngineFunctionMap.Map map, string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("# game\tplane\tid\tkind\ttitle\tcategory\tclass\tfunctions\r\n");
            foreach (EngineFunctionMap.Entry e in map.Entries)
            {
                sb.Append(e.Command.Game).Append('\t').Append(e.Command.Plane).Append('\t').Append(e.Command.Id).Append('\t')
                  .Append(e.Command.IsAxis ? "axis" : "button").Append('\t').Append(Clean(e.Command.Title)).Append('\t').Append(Clean(e.Command.Category)).Append('\t')
                  .Append(e.Class).Append('\t')
                  .Append(string.Join(";", e.Functions.Select(a => a.Function.Id + (a.Dir.Length > 0 ? ":" + a.Dir : string.Empty) + "@" +
                                                                    a.Weight.ToString("0.##", CultureInfo.InvariantCulture) + "/" + a.Source)))
                  .Append("\r\n");
            }
            string folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static string Clean(string text)
        {
            return (text ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        // query -> function ids + the matching actions of every game, grouped by game (best first).
        public static EngineResolveResult Resolve(string query, ICollection<string> games, ICollection<string> planes, bool? axis, int perGame)
        {
            Stopwatch watch = Stopwatch.StartNew();
            EngineResolveResult result = new EngineResolveResult { Query = (query ?? string.Empty).Trim() };
            EngineFunctionMap.Map map = EngineFunctionMap.Load(result.Warnings);
            EngineFunctionMap.Understanding u = EngineFunctionMap.Resolve(map, result.Query);
            result.Direction = u.Dir;
            result.Extra = u.Extra.ToList();
            result.DidYouMean = u.DidYouMean;
            HashSet<string> gameFilter = new HashSet<string>((games ?? new List<string>()).Select(EngineRelationSuggest.GameKey).Where(g => g.Length > 0), StringComparer.OrdinalIgnoreCase);
            HashSet<string> planeFilter = new HashSet<string>((planes ?? new List<string>()).Select(p => p.Trim()).Where(p => p.Length > 0), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, EngineResolvedAction> rows = new Dictionary<string, EngineResolvedAction>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> rank = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            // Canonical axes rank first only for a query about one function ("pitch", not "cut the throttle").
            bool single = u.Functions.Count(x => !x.Related) == 1;
            foreach (EngineFunctionMap.Resolved r in u.Functions)
            {
                result.Functions.Add(new EngineResolvedFunction
                {
                    Id = r.Function.Id,
                    Name = r.Function.Name,
                    Category = r.Function.Category,
                    Kind = r.Function.Kind,
                    Dir = r.Dir,
                    Term = r.Term,
                    Score = Math.Round(r.Score, 1),
                    Related = r.Related,
                    Described = r.Described,
                    Synonyms = r.Function.Synonyms.ToList()
                });
                List<EngineFunctionMap.Entry> entries;
                if (!map.ByFunction.TryGetValue(r.Function.Id, out entries))
                    continue;
                // The function's canonical axis first (MSFS "AXIS ELEVATOR SET" for "pitch"), when the query names the
                // function by its main word or describes it (by a variant's word, "collective", only before the other rows
                // of the same weight).
                bool mainTerm = (r.Described || EngineFunctionMap.Tokenize(r.Term).Intersect(EngineFunctionMap.Tokenize(r.Function.Name)).Any());
                foreach (EngineFunctionMap.Entry e in entries)
                {
                    EngineRelationSuggest.Command c = e.Command;
                    if ((gameFilter.Count > 0 && !gameFilter.Contains(c.Game)) || (planeFilter.Count > 0 && !planeFilter.Contains(c.Plane)) || (axis.HasValue && c.IsAxis != axis.Value))
                        continue;
                    EngineFunctionMap.Assignment a = e.Of(r.Function.Id);
                    if (EngineIdMeanings.CatalogAgainstTitle(e, a))
                        continue;
                    if (r.Dir.Length > 0 && !c.IsAxis && a.Dir.Length > 0 && !string.Equals(a.Dir, r.Dir, StringComparison.OrdinalIgnoreCase) && a.Dir != "toggle")
                        continue;
                    double score = (r.Related ? 0.6 : 1.0) * a.Weight * 100 + (c.IsAxis == r.Function.AxisKind ? 20 : 0) +
                                   (r.Dir.Length > 0 && string.Equals(a.Dir, r.Dir, StringComparison.OrdinalIgnoreCase) ? 10 : 0);
                    int primary = single && !r.Related && (r.Dir.Length == 0 || string.Equals(a.Dir, r.Dir, StringComparison.OrdinalIgnoreCase)) ? EngineFunctionMap.PrimaryRank(e, r.Function) : -1;
                    if (primary >= 0)
                        score += mainTerm ? 30 - Math.Min(primary, 30) * 0.5 : 3 - Math.Min(primary, 30) * 0.05;
                    // One row per id and meaning (EngineIdMeanings): an aircraft whose title of the id means something
                    // else is a row of its own (and only there when it is this function too).
                    int cluster = EngineIdMeanings.ClusterIndex(c.Game, c.Id, c.Plane);
                    string key = c.Game + "|" + c.Id + "|" + (cluster >= 0 ? cluster.ToString(CultureInfo.InvariantCulture) : "t:" + c.Title);
                    EngineResolvedAction row;
                    if (!rows.TryGetValue(key, out row))
                    {
                        row = new EngineResolvedAction
                        {
                            Game = c.Game, Id = c.Id, Title = c.Title, Category = c.Category, IsAxis = c.IsAxis,
                            Function = r.Function.Id, Dir = a.Dir, Weight = a.Weight, Source = a.Source, Term = a.Term,
                            Why = "Same function: " + EngineFunctionMap.Describe(r.Function, a.Dir, c.IsAxis) + (a.Source == "catalog" ? " (function catalog)" : " ('" + a.Term + "')")
                        };
                        rows[key] = row;
                        rank[key] = score;
                    }
                    else if (score > rank[key])
                    {
                        rank[key] = score;
                    }
                    if (!row.Planes.Contains(c.Plane, StringComparer.OrdinalIgnoreCase))
                        row.Planes.Add(c.Plane);
                }
            }
            foreach (IGrouping<string, KeyValuePair<string, EngineResolvedAction>> game in rows.GroupBy(r => r.Value.Game)
                         .OrderByDescending(g => g.Max(r => rank[r.Key])).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                EngineResolveGame group = new EngineResolveGame { Game = game.Key, Count = game.Count() };
                foreach (KeyValuePair<string, EngineResolvedAction> row in game.OrderByDescending(r => rank[r.Key]).ThenByDescending(r => r.Value.Planes.Count).ThenBy(r => r.Value.Title, StringComparer.OrdinalIgnoreCase))
                {
                    if (perGame > 0 && group.Actions.Count >= perGame)
                        break;
                    row.Value.Planes.Sort(StringComparer.OrdinalIgnoreCase);
                    if (row.Value.Game == "DCS")
                    {
                        int near;
                        int different;
                        row.Value.Variants = EngineIdMeanings.Describe(row.Value.Game, row.Value.Id, row.Value.Planes, out near, out different);
                        row.Value.Near = near;
                        row.Value.SameIdDifferent = different;
                        row.Value.MeaningNote = EngineRelationSearch.MeaningNote(near, different);
                        EngineAircraftVariant head = row.Value.Variants.FirstOrDefault(v => v.Included);
                        if (head != null)
                        {
                            row.Value.Title = head.Title;
                            row.Value.Category = head.Category;
                        }
                    }
                    group.Actions.Add(row.Value);
                }
                result.Games.Add(group);
            }
            result.Total = rows.Count;
            watch.Stop();
            result.ElapsedMilliseconds = watch.ElapsedMilliseconds;
            return result;
        }

        public static string ToText(EngineFunctionListResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Function map: " + result.Totals.Functions.ToString(CultureInfo.InvariantCulture) + " functions, " +
                          result.Totals.Synonyms.ToString(CultureInfo.InvariantCulture) + " names and synonyms, " + result.Totals.Terms.ToString(CultureInfo.InvariantCulture) +
                          " game terms; " + result.Totals.Mapped.ToString(CultureInfo.InvariantCulture) + " of " + result.Totals.Actions.ToString(CultureInfo.InvariantCulture) +
                          " actions mapped (" + result.LoadMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms, " +
                          result.ReadMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms of it reading the game lists)");
            foreach (IGrouping<string, EngineFunctionInfo> category in result.Functions.GroupBy(f => f.CategoryName))
            {
                sb.AppendLine(category.Key + ":");
                foreach (EngineFunctionInfo f in category)
                {
                    sb.AppendLine("  - " + f.Id + " | " + f.Name + " | " + f.Kind + " | " + f.Actions.ToString(CultureInfo.InvariantCulture) + " actions | " +
                                  string.Join(", ", f.Synonyms.Take(12)) + (f.Synonyms.Count > 12 ? ", ..." : string.Empty));
                }
            }
            if (result.Coverage.Count > 0)
            {
                sb.AppendLine("Coverage:");
                foreach (EngineFunctionCoverage c in result.Coverage)
                {
                    sb.AppendLine("  - " + c.Game + " | " + c.Mapped.ToString(CultureInfo.InvariantCulture) + "/" + c.Actions.ToString(CultureInfo.InvariantCulture) + " (" +
                                  c.Percent.ToString("0.0", CultureInfo.InvariantCulture) + "%) | catalog " + c.Catalog.ToString(CultureInfo.InvariantCulture) + ", rules " +
                                  c.Rules.ToString(CultureInfo.InvariantCulture) + " | " + c.Functions.ToString(CultureInfo.InvariantCulture) + " functions | " +
                                  c.Planes.ToString(CultureInfo.InvariantCulture) + " aircraft/layers");
                }
            }
            foreach (string warning in result.Warnings.Distinct().Where(w => !w.Contains("action catalog not found")).Take(10))
                sb.AppendLine("Warning: " + warning);
            return sb.ToString();
        }

        public static string ToText(EngineResolveResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Query: " + result.Query + (result.DidYouMean.Length > 0 ? " (did you mean: " + result.DidYouMean + ")" : string.Empty));
            sb.AppendLine("Functions: " + (result.Functions.Count == 0 ? "(none)" : string.Join(", ", result.Functions.Select(f => f.Id + (f.Dir.Length > 0 ? ":" + f.Dir : string.Empty) +
                                                                                                                                 " (" + f.Name + (f.Related ? ", related" : f.Described ? ", described" : string.Empty) + ", '" + f.Term + "')"))));
            if (result.Extra.Count > 0)
                sb.AppendLine("Other words: " + string.Join(" ", result.Extra));
            sb.AppendLine("Actions: " + result.Total.ToString(CultureInfo.InvariantCulture) + " in " + result.Games.Count.ToString(CultureInfo.InvariantCulture) + " games");
            foreach (EngineResolveGame game in result.Games)
            {
                sb.AppendLine(game.Game + " (" + game.Count.ToString(CultureInfo.InvariantCulture) + "):");
                foreach (EngineResolvedAction a in game.Actions)
                {
                    sb.AppendLine("  - " + a.Id + " | " + a.Title + (a.IsAxis ? " | axis" : string.Empty) + " | " + a.Function + (a.Dir.Length > 0 ? ":" + a.Dir : string.Empty) +
                                  "@" + a.Weight.ToString("0.##", CultureInfo.InvariantCulture) + " " + a.Source +
                                  (a.Game == "DCS" ? " | " + string.Join("|", a.Planes.Take(8)) + (a.Planes.Count > 8 ? " +" + (a.Planes.Count - 8).ToString(CultureInfo.InvariantCulture) : string.Empty) : string.Empty));
                }
            }
            foreach (string warning in result.Warnings.Distinct().Where(w => !w.Contains("action catalog not found")).Take(10))
                sb.AppendLine("Warning: " + warning);
            return sb.ToString();
        }
    }

    public sealed class EngineFunctionInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string CategoryName { get; set; }
        public string Kind { get; set; }
        public string Only { get; set; }
        public string Description { get; set; }
        public List<string> Synonyms { get; set; }
        public Dictionary<string, List<string>> Terms { get; set; }
        public List<string> Directions { get; set; }
        public List<string> Slots { get; set; }
        public List<string> Related { get; set; }
        public int Actions { get; set; }
        public Dictionary<string, int> Games { get; set; }

        public EngineFunctionInfo()
        {
            Id = Name = Category = CategoryName = Kind = Only = Description = string.Empty;
            Synonyms = new List<string>();
            Terms = new Dictionary<string, List<string>>();
            Directions = new List<string>();
            Slots = new List<string>();
            Related = new List<string>();
            Games = new Dictionary<string, int>();
        }
    }

    public sealed class EngineFunctionTotals
    {
        public int Functions { get; set; }
        public int Synonyms { get; set; }
        public int Terms { get; set; }
        public int Actions { get; set; }
        public int Mapped { get; set; }
    }

    public sealed class EngineFunctionCoverage
    {
        public string Game { get; set; }
        public int Planes { get; set; }
        public int Actions { get; set; }
        public int Mapped { get; set; }
        public int Catalog { get; set; }
        public int Rules { get; set; }
        public double Percent { get; set; }
        public int Functions { get; set; }
        public List<string> Unmapped { get; set; }

        public EngineFunctionCoverage()
        {
            Game = string.Empty;
            Unmapped = new List<string>();
        }
    }

    public sealed class EngineFunctionListResult
    {
        public int Version { get; set; }
        public string Source { get; set; }
        public EngineFunctionTotals Totals { get; set; }
        public Dictionary<string, string> Categories { get; set; }
        public List<EngineFunctionInfo> Functions { get; set; }
        public List<EngineFunctionCoverage> Coverage { get; set; }
        public long LoadMilliseconds { get; set; }
        public long ReadMilliseconds { get; set; }
        // The mapped index came from <data>\Cache\function-map.bin (the game lists did not change).
        public bool FromCache { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public List<string> Warnings { get; set; }

        public EngineFunctionListResult()
        {
            Source = string.Empty;
            Totals = new EngineFunctionTotals();
            Categories = new Dictionary<string, string>();
            Functions = new List<EngineFunctionInfo>();
            Coverage = new List<EngineFunctionCoverage>();
            Warnings = new List<string>();
        }
    }

    public sealed class EngineResolvedFunction
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Kind { get; set; }
        public string Dir { get; set; }
        public string Term { get; set; }
        public double Score { get; set; }
        public bool Related { get; set; }
        public bool Described { get; set; }
        public List<string> Synonyms { get; set; }

        public EngineResolvedFunction()
        {
            Id = Name = Category = Kind = Dir = Term = string.Empty;
            Synonyms = new List<string>();
        }
    }

    public sealed class EngineResolvedAction
    {
        public string Game { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public string Category { get; set; }
        public bool IsAxis { get; set; }
        public List<string> Planes { get; set; }
        public string Function { get; set; }
        public string Dir { get; set; }
        public double Weight { get; set; }
        public string Source { get; set; }
        public string Term { get; set; }
        public string Why { get; set; }
        // DCS: every meaning of the id (EngineIdMeanings), the row's own Included; see EngineRelationSearchHit.
        public List<EngineAircraftVariant> Variants { get; set; }
        public int Near { get; set; }
        public int SameIdDifferent { get; set; }
        public string MeaningNote { get; set; }

        public EngineResolvedAction()
        {
            Game = Id = Title = Category = Function = Dir = Source = Term = Why = MeaningNote = string.Empty;
            Planes = new List<string>();
            Variants = new List<EngineAircraftVariant>();
        }
    }

    public sealed class EngineResolveGame
    {
        public string Game { get; set; }
        public int Count { get; set; }
        public List<EngineResolvedAction> Actions { get; set; }

        public EngineResolveGame()
        {
            Game = string.Empty;
            Actions = new List<EngineResolvedAction>();
        }
    }

    public sealed class EngineResolveResult
    {
        public string Query { get; set; }
        public List<EngineResolvedFunction> Functions { get; set; }
        public string Direction { get; set; }
        public string DidYouMean { get; set; }
        public List<string> Extra { get; set; }
        public List<EngineResolveGame> Games { get; set; }
        public int Total { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public EngineResolveResult()
        {
            Query = Direction = DidYouMean = string.Empty;
            Functions = new List<EngineResolvedFunction>();
            Extra = new List<string>();
            Games = new List<EngineResolveGame>();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }
}
