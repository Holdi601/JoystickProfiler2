using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    // engine relations phrase-check --file <phrases.json> [--entries search|catalog|assistant|plan|describe]
    //   [--functions id1|id2] [--kinds phrases|descriptions|de] [--failures N]
    // What players type - names, synonyms, slang, paraphrases, everyday sentences, German - must find the function on
    // every entry point, offline (no model): engine/tests/fixtures/phrases/phrases.json has a phrase set per function
    // of the function map. Per entry point:
    //   search     the Add actions search: the query's top function (top-2 for a phrase marked ambiguous_with) and, per
    //              game that has the function, its top 3 rows belong to it (precision; the opposite direction counts as
    //              wrong); games without it that still show rows are noise;
    //   catalog    the catalog filter of one game's list: per game that has the function, its first row is it;
    //   assistant  the JoyPro assistant's resolve: most of its actions are the function;
    //   plan       Manage / Edit with AI "make a relation for <phrase>": most of the planned actions are it;
    //   describe   By description: most of the proposed actions are it.
    // Nothing changes (the profile in memory is not touched; plan and describe run as dry runs).
    internal static class EnginePhraseCheck
    {
        internal sealed class Phrase
        {
            public string Function = string.Empty;
            public string Text = string.Empty;
            public string Kind = string.Empty;
            public List<string> Alternatives = new List<string>();
        }

        public static readonly string[] Entries = { "search", "catalog", "assistant", "plan", "describe" };

        public static List<Phrase> Load(string file, List<string> errors)
        {
            List<Phrase> list = new List<Phrase>();
            Dictionary<string, object> json;
            try
            {
                json = EngineSerialization.FromJson<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                errors.Add("Phrase file not readable: " + ex.Message);
                return list;
            }
            foreach (KeyValuePair<string, object> pair in json)
            {
                Dictionary<string, object> set = pair.Value as Dictionary<string, object>;
                if (pair.Key.StartsWith("_", StringComparison.Ordinal) || set == null)
                    continue;
                List<string> alternatives = EngineNlGroup.Strings(EngineNlGroup.Get(set, "ambiguous_with")).ToList();
                foreach (string kind in new[] { "phrases", "descriptions", "de" })
                {
                    foreach (string text in EngineNlGroup.Strings(EngineNlGroup.Get(set, kind)).Where(t => t.Trim().Length > 0))
                        list.Add(new Phrase { Function = pair.Key, Text = text.Trim(), Kind = kind, Alternatives = alternatives });
                }
            }
            return list;
        }

        public static EnginePhraseCheckResult Run(string file, ICollection<string> entries, ICollection<string> functions, ICollection<string> kinds, int failures)
        {
            Stopwatch watch = Stopwatch.StartNew();
            EnginePhraseCheckResult result = new EnginePhraseCheckResult();
            List<Phrase> phrases = Load(file, result.Errors);
            if (functions != null && functions.Count > 0)
                phrases = phrases.Where(p => functions.Contains(p.Function, StringComparer.OrdinalIgnoreCase)).ToList();
            if (kinds != null && kinds.Count > 0)
                phrases = phrases.Where(p => kinds.Contains(p.Kind, StringComparer.OrdinalIgnoreCase)).ToList();
            HashSet<string> wanted = new HashSet<string>(entries != null && entries.Count > 0 ? entries : Entries, StringComparer.OrdinalIgnoreCase);
            EngineFunctionMap.Map map = EngineFunctionMap.Load(result.Warnings);
            // Games that have each function (its actions of either kind).
            // Games that have each function, with how many distinct actions of it.
            Dictionary<string, Dictionary<string, int>> gamesOf = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<EngineFunctionMap.Entry>> pair in map.ByFunction)
                gamesOf[pair.Key] = pair.Value.Where(e => e.Of(pair.Key) != null && e.Of(pair.Key).Weight >= 0.5).GroupBy(e => e.Command.Game, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Command.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(), StringComparer.OrdinalIgnoreCase);
            result.Phrases = phrases.Count;
            foreach (string function in phrases.Select(p => p.Function).Distinct(StringComparer.OrdinalIgnoreCase))
                result.FunctionGames[function] = gamesOf.ContainsKey(function) ? gamesOf[function].Keys.OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
            result.Functions = phrases.Select(p => p.Function).Distinct().Count();
            foreach (string entry in Entries.Where(wanted.Contains))
                result.Entry.Add(new EnginePhraseEntry { Entry = entry });
            foreach (Phrase phrase in phrases)
            {
                Dictionary<string, int> has;
                if (!gamesOf.TryGetValue(phrase.Function, out has))
                    has = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                EngineRelationSearchResult search = null;
                if (wanted.Contains("search") || wanted.Contains("catalog"))
                {
                    search = EngineRelationSearch.Search(phrase.Text, null, null, null, 0, string.Empty, 6);
                    // A phrase with a direction ("speed up time") counts only the games that have that direction (or a
                    // toggle, or an axis): Nuclear Option's slow motion is no time acceleration up.
                    string dir = DirOf(search, phrase.Function);
                    List<EngineFunctionMap.Entry> functionEntries;
                    if (dir.Length > 0 && map.ByFunction.TryGetValue(phrase.Function, out functionEntries))
                    {
                        has = functionEntries.Where(e => { EngineFunctionMap.Assignment a = e.Of(phrase.Function); return a != null && a.Weight >= 0.5 && (e.Command.IsAxis || a.Dir.Length == 0 || a.Dir == "toggle" || string.Equals(a.Dir, dir, StringComparison.OrdinalIgnoreCase)); })
                            .GroupBy(e => e.Command.Game, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(e => e.Command.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(), StringComparer.OrdinalIgnoreCase);
                    }
                }
                foreach (EnginePhraseEntry entry in result.Entry)
                {
                    string got;
                    bool pass;
                    switch (entry.Entry)
                    {
                        case "search":
                            pass = SearchPass(map, phrase, search, has, entry, out got);
                            break;
                        case "catalog":
                            pass = CatalogPass(phrase, search, has, entry, out got);
                            break;
                        case "assistant":
                            pass = AssistantPass(phrase, out got);
                            break;
                        case "plan":
                            pass = PlanPass(map, phrase, out got);
                            break;
                        default:
                            pass = DescribePass(map, phrase, out got);
                            break;
                    }
                    if (entry.Entry != "catalog")
                    {
                        entry.Cells++;
                        if (pass)
                            entry.Pass++;
                        Count(entry.ByKind, phrase.Kind, pass);
                        Count(entry.ByFunction, phrase.Function, pass);
                    }
                    if (!pass && entry.Failures.Count < failures)
                        entry.Failures.Add(phrase.Function + " | " + phrase.Kind + " | \"" + phrase.Text + "\" -> " + got);
                }
            }
            foreach (EnginePhraseEntry entry in result.Entry)
            {
                entry.PassRate = entry.Cells == 0 ? 0 : Math.Round(100.0 * entry.Pass / entry.Cells, 1);
                entry.Precision = entry.Rows == 0 ? 0 : Math.Round(100.0 * entry.GoodRows / entry.Rows, 1);
                foreach (KeyValuePair<string, int[]> game in entry.ByGameRows)
                    entry.PrecisionByGame[game.Key] = game.Value[1] == 0 ? 0 : Math.Round(100.0 * game.Value[0] / game.Value[1], 1);
                entry.ByGameRows.Clear();
            }
            result.ElapsedMs = watch.ElapsedMilliseconds;
            return result;
        }

        private static void Count(Dictionary<string, int[]> counts, string key, bool pass)
        {
            int[] c;
            if (!counts.TryGetValue(key, out c))
                counts[key] = c = new int[2];
            if (pass)
                c[0]++;
            c[1]++;
        }

        // The function ranks first (or second for a phrase that may mean another function too).
        private static bool TopFunction(Phrase phrase, IList<string> ranked, out string got)
        {
            got = ranked.Count == 0 ? "no function" : string.Join(", ", ranked.Take(3));
            int at = ranked.IndexOf(phrase.Function);
            if (at == 0)
                return true;
            return at == 1 && phrase.Alternatives.Count > 0 && phrase.Alternatives.Contains(ranked[0], StringComparer.OrdinalIgnoreCase);
        }

        private static bool RowIs(EngineRelationSearchHit hit, Phrase phrase, string dir)
        {
            foreach (string f in hit.Functions ?? new List<string>())
            {
                string[] parts = f.Split(':');
                // A phrase that may also mean another function: that one's rows are fine too.
                if (!string.Equals(parts[0], phrase.Function, StringComparison.OrdinalIgnoreCase))
                {
                    if (phrase.Alternatives.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
                        return true;
                    continue;
                }
                string rowDir = parts.Length > 1 ? parts[1] : string.Empty;
                // The opposite direction is wrong ("gear up" listing gear down); a toggle, no direction or another one is fine.
                if (dir.Length > 0 && rowDir.Length > 0 && Opposite(dir, rowDir))
                    return false;
                return true;
            }
            return false;
        }

        private static readonly string[][] OppositePairs =
        {
            new[] { "up", "down" }, new[] { "left", "right" }, new[] { "fwd", "aft" }, new[] { "forward", "back" }, new[] { "fwd", "back" }, new[] { "in", "out" },
            new[] { "on", "off" }, new[] { "increase", "decrease" }, new[] { "extend", "retract" }, new[] { "open", "close" }, new[] { "next", "prev" },
            new[] { "cw", "ccw" }, new[] { "arm", "safe" }, new[] { "noseup", "nosedown" }, new[] { "rich", "lean" }
        };

        internal static bool Opposite(string a, string b)
        {
            return OppositePairs.Any(p => (string.Equals(p[0], a, StringComparison.OrdinalIgnoreCase) && string.Equals(p[1], b, StringComparison.OrdinalIgnoreCase)) ||
                                          (string.Equals(p[1], a, StringComparison.OrdinalIgnoreCase) && string.Equals(p[0], b, StringComparison.OrdinalIgnoreCase)));
        }

        private static string DirOf(EngineRelationSearchResult search, string function)
        {
            EngineSearchFunction f = search.Resolved.FirstOrDefault(r => string.Equals(r.Id, function, StringComparison.OrdinalIgnoreCase));
            return f == null ? string.Empty : f.Dir ?? string.Empty;
        }

        private static bool SearchPass(EngineFunctionMap.Map map, Phrase phrase, EngineRelationSearchResult search, Dictionary<string, int> has, EnginePhraseEntry entry, out string got)
        {
            List<string> ranked = search.Resolved.Where(r => !r.Related).Select(r => r.Id).Distinct().ToList();
            bool top = TopFunction(phrase, ranked, out got);
            string dir = DirOf(search, phrase.Function);
            int good = 0, rows = 0;
            foreach (IGrouping<string, EngineRelationSearchHit> game in search.Results.GroupBy(h => h.Game))
            {
                int own;
                if (!has.TryGetValue(game.Key, out own))
                {
                    entry.NoiseGames++;
                    continue;
                }
                // Its top rows, as many as the game has actions of the function (at most 3).
                List<EngineRelationSearchHit> topRows = game.OrderByDescending(h => h.Score).Take(Math.Max(1, Math.Min(3, own))).ToList();
                int[] counts;
                if (!entry.ByGameRows.TryGetValue(game.Key, out counts))
                    entry.ByGameRows[game.Key] = counts = new int[2];
                foreach (EngineRelationSearchHit hit in topRows)
                {
                    bool ok = RowIs(hit, phrase, dir);
                    rows++;
                    counts[1]++;
                    if (ok)
                    {
                        good++;
                        counts[0]++;
                    }
                }
            }
            entry.Rows += rows;
            entry.GoodRows += good;
            if (!top)
                return false;
            if (rows > 0 && good * 10 < rows * 8)
            {
                got = "top function ok, but only " + good.ToString(CultureInfo.InvariantCulture) + " of " + rows.ToString(CultureInfo.InvariantCulture) + " top rows are it";
                return false;
            }
            return true;
        }

        // One game's list filtered by the phrase: its first row is the function (per game that has it).
        private static bool CatalogPass(Phrase phrase, EngineRelationSearchResult search, Dictionary<string, int> has, EnginePhraseEntry entry, out string got)
        {
            string dir = DirOf(search, phrase.Function);
            int pass = 0, cells = 0;
            List<string> wrong = new List<string>();
            foreach (string game in has.Keys)
            {
                EngineRelationSearchHit first = search.Results.Where(h => h.Game == game).OrderByDescending(h => h.Score).FirstOrDefault();
                bool ok = first != null && RowIs(first, phrase, dir);
                cells++;
                if (ok)
                    pass++;
                else if (wrong.Count < 3)
                    wrong.Add(game + ": " + (first == null ? "nothing" : "\"" + first.Title + "\""));
                Count(entry.ByKind, phrase.Kind, ok);
                Count(entry.ByFunction, phrase.Function, ok);
                int[] counts;
                if (!entry.ByGameRows.TryGetValue(game, out counts))
                    entry.ByGameRows[game] = counts = new int[2];
                counts[1]++;
                entry.Rows++;
                if (ok)
                {
                    counts[0]++;
                    entry.GoodRows++;
                }
            }
            entry.Cells += cells;
            entry.Pass += pass;
            got = wrong.Count == 0 ? "ok" : string.Join("; ", wrong);
            return pass == cells;
        }

        // The function most of the actions are (each action counts once, its strongest function).
        private static List<string> Majority(EngineFunctionMap.Map map, IEnumerable<KeyValuePair<string, string>> actions, string expected)
        {
            Dictionary<string, int> votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> action in actions)
            {
                EngineFunctionMap.Entry e;
                if (!map.ByKey.TryGetValue(action.Key, out e))
                    continue;
                foreach (EngineFunctionMap.Assignment a in e.Functions.Where(x => x.Weight >= 0.5))
                    votes[a.Function.Id] = (votes.ContainsKey(a.Function.Id) ? votes[a.Function.Id] : 0) + 1;
            }
            List<string> ranked = votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal).Select(v => v.Key).ToList();
            // Multi-purpose buttons (DCS "NWS / A/R DISC / MSL STEP") vote for several functions: the expected one counts as
            // first when it has nearly as many votes as the first.
            int expectedVotes;
            if (ranked.Count > 0 && votes.TryGetValue(expected, out expectedVotes) && expectedVotes >= 0.8 * votes[ranked[0]])
            {
                ranked.Remove(expected);
                ranked.Insert(0, expected);
            }
            return ranked;
        }

        private static bool AssistantPass(Phrase phrase, out string got)
        {
            string understood;
            List<EngineRelationSearchHit> hits = EngineRelationAssistantBrain.Resolve(phrase.Text, null, null, new List<string>(), out understood);
            EngineFunctionMap.Map map = EngineFunctionMap.Load(new List<string>());
            List<string> ranked = Majority(map, hits.SelectMany(h => (h.Game == "DCS" ? h.Planes.Take(1) : new List<string> { h.Game }).Select(p => new KeyValuePair<string, string>(h.Game + "|" + h.Id + "|" + p, h.Title))), phrase.Function);
            return TopFunction(phrase, ranked, out got);
        }

        private static bool PlanPass(EngineFunctionMap.Map map, Phrase phrase, out string got)
        {
            List<EngineNlGroupNode> nodes;
            bool isAxis;
            string function;
            string problem = EngineRelationPlan.ResolveDescription(phrase.Text, string.Empty, out nodes, out isAxis, out function);
            if (problem.Length > 0)
            {
                got = problem;
                return false;
            }
            if (TopFunction(phrase, Majority(map, NodeKeys(nodes), phrase.Function), out got))
                return true;
            // The word rules' catalog slot is the function's ("china hat" = the China hat slot, whatever else its
            // multi-purpose buttons do).
            EngineNlGroupResult rules = EngineNlGroup.Run(new EngineNlGroup.Options { Request = phrase.Text, DryRun = true, LlmMode = "off" });
            return SlotOf(map, phrase, rules.Proposals.FirstOrDefault());
        }

        private static bool SlotOf(EngineFunctionMap.Map map, Phrase phrase, EngineNlGroupProposal first)
        {
            EngineFunctionMap.Function f;
            if (first == null || first.Slots.Count == 0 || !map.ById.TryGetValue(phrase.Function, out f))
                return false;
            return f.Slots.Any(l => first.Slots.Contains(l.Slot, StringComparer.OrdinalIgnoreCase));
        }

        private static bool DescribePass(EngineFunctionMap.Map map, Phrase phrase, out string got)
        {
            EngineNlGroupResult result = EngineNlGroup.Run(new EngineNlGroup.Options { Request = phrase.Text, DryRun = true, LlmMode = "off" });
            List<EngineNlGroupNode> nodes = result.Proposals.SelectMany(p => p.Nodes).ToList();
            if (nodes.Count == 0)
            {
                got = result.Errors.Count > 0 ? result.Errors[0] : "nothing proposed";
                return false;
            }
            return TopFunction(phrase, Majority(map, NodeKeys(nodes), phrase.Function), out got) || SlotOf(map, phrase, result.Proposals.FirstOrDefault());
        }

        private static IEnumerable<KeyValuePair<string, string>> NodeKeys(IEnumerable<EngineNlGroupNode> nodes)
        {
            foreach (EngineNlGroupNode node in nodes)
            {
                string game = EngineRelationSuggest.GameKey(node.Game ?? string.Empty);
                foreach (string plane in (game == "DCS" ? node.Planes.Take(1) : new List<string> { game }))
                    yield return new KeyValuePair<string, string>(game + "|" + EngineRelationSuggest.NodeId(game, node.Id) + "|" + plane, node.Id);
            }
        }

        public static string ToText(EnginePhraseCheckResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Phrase check: " + result.Phrases.ToString(CultureInfo.InvariantCulture) + " phrases of " + result.Functions.ToString(CultureInfo.InvariantCulture) +
                          " functions (" + (result.ElapsedMs / 1000).ToString(CultureInfo.InvariantCulture) + " s)");
            foreach (EnginePhraseEntry entry in result.Entry)
            {
                sb.AppendLine("  " + entry.Entry.PadRight(10) + " pass " + entry.PassRate.ToString("0.0", CultureInfo.InvariantCulture) + "% (" + entry.Pass + "/" + entry.Cells + ")" +
                              (entry.Rows > 0 ? ", precision " + entry.Precision.ToString("0.0", CultureInfo.InvariantCulture) + "% (" + entry.GoodRows + "/" + entry.Rows + ")" : string.Empty) +
                              (entry.Entry == "search" ? ", noise games " + entry.NoiseGames : string.Empty) +
                              " | " + string.Join(", ", entry.ByKind.Select(k => k.Key + " " + Math.Round(100.0 * k.Value[0] / Math.Max(1, k.Value[1]), 1).ToString("0.0", CultureInfo.InvariantCulture) + "%")));
            }
            foreach (EnginePhraseEntry entry in result.Entry.Where(e => e.PrecisionByGame.Count > 0))
                sb.AppendLine("  " + entry.Entry + " precision per game: " + string.Join(", ", entry.PrecisionByGame.OrderBy(g => g.Key).Select(g => g.Key + " " + g.Value.ToString("0", CultureInfo.InvariantCulture))));
            foreach (EnginePhraseEntry entry in result.Entry)
            {
                List<string> worst = entry.ByFunction.Where(f => f.Value[0] < f.Value[1]).OrderBy(f => (double)f.Value[0] / f.Value[1]).Take(25)
                    .Select(f => f.Key + " " + f.Value[0] + "/" + f.Value[1]).ToList();
                if (worst.Count > 0)
                    sb.AppendLine("  " + entry.Entry + " weakest functions: " + string.Join(", ", worst));
                foreach (string failure in entry.Failures)
                    sb.AppendLine("    FAIL " + entry.Entry + " | " + failure);
            }
            foreach (string error in result.Errors)
                sb.AppendLine("Error: " + error);
            return sb.ToString();
        }
    }

    public sealed class EnginePhraseEntry
    {
        public string Entry { get; set; }
        public int Cells { get; set; }
        public int Pass { get; set; }
        public double PassRate { get; set; }
        public int Rows { get; set; }
        public int GoodRows { get; set; }
        public double Precision { get; set; }
        public int NoiseGames { get; set; }
        public Dictionary<string, double> PrecisionByGame { get; set; }
        public Dictionary<string, int[]> ByKind { get; set; }
        public Dictionary<string, int[]> ByFunction { get; set; }
        internal Dictionary<string, int[]> ByGameRows = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        public List<string> Failures { get; set; }

        public EnginePhraseEntry()
        {
            Entry = string.Empty;
            PrecisionByGame = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            ByKind = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            ByFunction = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            Failures = new List<string>();
        }
    }

    public sealed class EnginePhraseCheckResult
    {
        public int Phrases { get; set; }
        public int Functions { get; set; }
        public long ElapsedMs { get; set; }
        public List<EnginePhraseEntry> Entry { get; set; }
        // Per function the games that have it (for samples of the catalog check).
        public Dictionary<string, List<string>> FunctionGames { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public EnginePhraseCheckResult()
        {
            Entry = new List<EnginePhraseEntry>();
            FunctionGames = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }

    internal static partial class Program
    {
        private static int PhraseCheckCommand(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            string file = PopOptionValue(args, "--file");
            List<string> entries = SplitList(PopOptionValues(args, "--entries"));
            List<string> functions = SplitList(PopOptionValues(args, "--functions"));
            List<string> kinds = SplitList(PopOptionValues(args, "--kinds"));
            string failuresRaw = PopOptionValue(args, "--failures");
            int failures = 40;
            if (failuresRaw != null && !int.TryParse(failuresRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out failures))
                return Fail("--failures takes a number.");
            if (string.IsNullOrWhiteSpace(file) || args.Count > 0)
                return Fail("relations phrase-check --file <phrases.json> [--entries search|catalog|assistant|plan|describe] [--functions id1|id2] [--kinds phrases|descriptions|de] [--failures N]");
            List<string> unknown = entries.Where(e => !EnginePhraseCheck.Entries.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0)
                return Fail("Unknown entry point(s): " + string.Join(", ", unknown) + ". Use " + string.Join(", ", EnginePhraseCheck.Entries) + ".");
            EnginePhraseCheckResult result = EnginePhraseCheck.Run(NormalizePath(file), entries, functions, kinds, failures);
            CommandChangedNothing = true;
            EmitPayload(textOutput ? EnginePhraseCheck.ToText(result) : EngineSerialization.ToJson(result), outPath);
            return result.Errors.Count > 0 ? 1 : 0;
        }
    }
}
