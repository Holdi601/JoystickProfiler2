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
    // Plan step "fill" (docs/ai-plans.md): every relation gets the actions of the named DCS aircraft that fit its
    // purpose. Two independent paths per relation x aircraft:
    //   1. rules: Suggest missing (function slots, catalog meanings, how the profile does it), always;
    //   2. the local AI model (optional): JoyPro shows it the relation (name, kind, its actions with title and cockpit
    //      device) and the aircraft's catalog candidates (rule picks plus a catalog search), the model picks one or
    //      none with a reason; only candidate ids count.
    // Both agree: high; one path only: medium (the path is named); different picks: ambiguous (unticked). The model
    // runs in small batches within a time cap, its picks are cached per relation x aircraft, progress on stderr.
    internal static class EngineRelationFill
    {
        public static int ModelSeconds = 120;
        public static bool CheckAll;
        private const int BatchSize = 5;
        private const int Candidates = 8;

        // ------------------------------------------------------------------------------------------------------
        // Aircraft names -> DCS aircraft ids ("F-15E Strike Eagle" -> F-15ESE, F-15E_WSO)
        // ------------------------------------------------------------------------------------------------------

        private static List<string> dcsPlanes;

        internal static List<string> DcsPlanes()
        {
            if (dcsPlanes == null)
            {
                dcsPlanes = EngineRelationSuggest.Index(new List<string>()).Where(c => c.Game == "DCS" && c.Plane.Length > 0)
                    .Select(c => c.Plane).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            }
            return dcsPlanes;
        }

        private static List<string> games;

        internal static List<string> Games()
        {
            if (games == null)
                games = EngineRelationSuggest.Index(new List<string>()).Select(c => c.Game).Where(g => g.Length > 0 && g != "DCS").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return games;
        }

        internal static string GameOf(string target) { int bar = target.IndexOf('|'); return bar < 0 ? "DCS" : target.Substring(0, bar); }

        internal static string PlaneOf(string target) { int bar = target.IndexOf('|'); return bar < 0 ? target : target.Substring(bar + 1); }

        private static string Norm(string value)
        {
            return Regex.Replace(value ?? string.Empty, "[^A-Za-z0-9]", string.Empty).ToLowerInvariant();
        }

        // The aircraft of one name: its designation (the first word with a digit) against the catalog ids.
        // Targets are "GAME|plane": a DCS aircraft ("DCS|F-15ESE") or a whole other game ("StarCitizen|StarCitizen").
        // Product names that are no aircraft id: Flaming Cliffs (2024 / 3) is its set of aircraft; the MiG-29A module is
        // MiG-29-Fulcrum (the FC MiG-29 is "mig-29").
        private static readonly Dictionary<string, string[]> Products = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "flamingcliffs2024", new[] { "a-10a", "F-15C", "J-11A", "mig-29", "mig-29c", "mig-29g", "su-25", "su-25T", "su-27", "su-33" } },
            { "fc2024", new[] { "a-10a", "F-15C", "J-11A", "mig-29", "mig-29c", "mig-29g", "su-25", "su-25T", "su-27", "su-33" } },
            { "flamingcliffs3", new[] { "a-10a", "F-15C", "J-11A", "mig-29", "mig-29c", "mig-29g", "su-25", "su-25T", "su-27", "su-33" } },
            { "fc3", new[] { "a-10a", "F-15C", "J-11A", "mig-29", "mig-29c", "mig-29g", "su-25", "su-25T", "su-27", "su-33" } },
            { "mig29a", new[] { "MiG-29-Fulcrum" } },
            { "mig29afulcrum", new[] { "MiG-29-Fulcrum" } },
            { "mig29fulcrum", new[] { "MiG-29-Fulcrum" } },
        };

        // "all modules in DCS", "every DCS aircraft" somewhere in a request.
        internal static bool MentionsAllDcs(string text)
        {
            return Regex.IsMatch(text ?? string.Empty,
                @"\b(?:all|every|each)\s+(?:of\s+)?(?:my\s+|the\s+|your\s+)?(?:(?:dcs(?:\s+world)?\s+)?(?:modules?|aircraft|airplanes?|planes?)\s+(?:in|of|for|from)\s+dcs(?:\s+world)?|dcs(?:\s+world)?\s+(?:modules?|aircraft|airplanes?|planes?))\b",
                RegexOptions.IgnoreCase);
        }

        // A name that means every DCS aircraft ("all DCS aircraft", "DCS", "all modules in DCS").
        internal static bool IsAllDcs(string name)
        {
            return Regex.IsMatch((name ?? string.Empty).Trim(),
                @"^(?:(?:all|every|each)\s+(?:of\s+)?)?(?:my\s+|the\s+|your\s+)?(?:dcs(?:\s+world)?(?:\s+(?:modules?|aircraft|airplanes?|planes?))?|(?:modules?|aircraft|airplanes?|planes?)\s+(?:in|of|for|from)\s+dcs(?:\s+world)?)$",
                RegexOptions.IgnoreCase);
        }

        public static List<string> Resolve(string name)
        {
            List<string> planes = DcsPlanes();
            if (IsAllDcs(name))
                return planes.Select(p => "DCS|" + p).ToList();
            string whole = Norm(name);
            if (Products.TryGetValue(whole, out string[] product))
            {
                List<string> set = product.SelectMany(id => planes.Where(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase))).Select(p => "DCS|" + p).ToList();
                if (set.Count > 0)
                    return set;
            }
            List<string> exactIds = planes.Where(p => Norm(p) == whole).ToList();
            if (exactIds.Count > 0)
            {
                // With its other seats (C-130J -> C-130J_LoadMaster, C-130J_UI).
                return planes.Where(p => exactIds.Any(id => string.Equals(p, id, StringComparison.OrdinalIgnoreCase) ||
                                                            p.StartsWith(id + "_", StringComparison.OrdinalIgnoreCase) ||
                                                            p.StartsWith(id + " ", StringComparison.OrdinalIgnoreCase)))
                    .Select(p => "DCS|" + p).ToList();
            }
            string game = Games().FirstOrDefault(g => whole.Length >= 3 && (Norm(g) == whole || Norm(EngineRelationAssistantGameNames.Label(g)) == whole));
            if (game != null)
                return new List<string> { game + "|" + game };
            // A game by any of its names ("xp12", "msfs" = 2024 and 2020, "sc", "Elite", "IL-2 1946").
            List<string> named = EngineGameWords.Resolve(name).Where(g => g != "DCS" && Games().Contains(g, StringComparer.OrdinalIgnoreCase)).ToList();
            if (named.Count > 0)
                return named.Select(g => g + "|" + g).ToList();
            foreach (string word in Regex.Split(name ?? string.Empty, @"[\s,;/()]+").Where(w => w.Any(char.IsDigit) && w.Any(char.IsLetter)))
            {
                string designation = Norm(word);
                if (designation.Length < 3)
                    continue;
                // Every seat and variant of it (F-15E, F-15ESE, F-15E_WSO).
                List<string> hits = planes.Where(p => Norm(p).StartsWith(designation, StringComparison.Ordinal)).ToList();
                if (hits.Count == 0)
                    hits = planes.Where(p => designation.StartsWith(Norm(p), StringComparison.Ordinal) && (Norm(p).Length >= 4 || designation.Length - Norm(p).Length == 1)).ToList();
                // A variant letter the catalog id leaves out ("F-14B" of F-14, its seats too).
                if (hits.Count > 0 && hits.All(p => Norm(p).Length < designation.Length))
                    hits = planes.Where(p => hits.Any(h => string.Equals(p, h, StringComparison.OrdinalIgnoreCase) || p.StartsWith(h + "_", StringComparison.OrdinalIgnoreCase))).ToList();
                // A designation inside the id ("F1EE" of Mirage-F1EE).
                if (hits.Count == 0)
                    hits = planes.Where(p => Regex.Split(p, @"[-_\s]+").Any(part => Norm(part).StartsWith(designation, StringComparison.Ordinal))).ToList();
                if (hits.Count > 0)
                    return hits.Select(p => "DCS|" + p).ToList();
            }
            return new List<string>();
        }

        // "... i.e. F-15E Strike Eagle, F-4E Phantom II, Flaming Cliffs 2024, ..." -> the aircraft and the names
        // JoyPro could not place.
        public static List<string> FromText(string text, List<string> unresolved)
        {
            List<string> found = new List<string>();
            // Also at a colon ("the new modules: Mirage F1EE, ...") and at the end of a sentence.
            foreach (string raw in Regex.Split(text ?? string.Empty, @",|;|:|\band\b|\bi\.e\.|\be\.g\.|\.\s+|\.$", RegexOptions.IgnoreCase))
            {
                string part = raw.Trim();
                if (part.Length == 0)
                    continue;
                List<string> planes = Resolve(part);
                foreach (string plane in planes)
                    if (!found.Contains(plane, StringComparer.OrdinalIgnoreCase))
                        found.Add(plane);
                // A short part that names something but matched nothing ("Flaming Cliffs 2024").
                if (planes.Count == 0 && part.Split(' ').Length <= 4 && Regex.IsMatch(part, @"^[A-Z0-9]"))
                    unresolved.Add(part);
            }
            return found;
        }

        // ------------------------------------------------------------------------------------------------------
        // The run
        // ------------------------------------------------------------------------------------------------------

        private sealed class Pair
        {
            public string Relation;
            public string Game;
            public string Plane;
            public List<EngineFillAction> Rules = new List<EngineFillAction>();
            public List<EngineFillAction> Offer = new List<EngineFillAction>();
            public int Evidence;
            public string Pick;
            public string Why = string.Empty;
            public bool Asked;
        }

        public static EngineFillResult Run(List<string> planes, List<string> relationNames, bool useModel, List<string> unresolved)
        {
            EngineFillResult result = new EngineFillResult { Aircraft = planes.ToList(), Unresolved = unresolved ?? new List<string>() };
            HashSet<string> targets = new HashSet<string>(planes, StringComparer.OrdinalIgnoreCase);
            List<string> names = (relationNames != null && relationNames.Count > 0 ? relationNames : InternalDataManagement.AllRelations.Keys.ToList())
                .Where(n => InternalDataManagement.AllRelations.ContainsKey(n)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            Dictionary<string, EngineRelationSuggest.Command> byKey = new Dictionary<string, EngineRelationSuggest.Command>(StringComparer.OrdinalIgnoreCase);
            foreach (EngineRelationSuggest.Command command in EngineRelationSuggest.Index(result.Warnings))
            {
                string key = command.Game + "|" + command.Id + "|" + command.Plane;
                if (!byKey.ContainsKey(key))
                    byKey[key] = command;
            }
            Stopwatch watch = Stopwatch.StartNew();
            // Path 1a: Auto Group's slot matching (the manual-checked function catalog), restricted to the targets, run
            // on the profile in memory and taken back at once: what it would add to the existing relations.
            EngineProgress.Step(0, 2, "Matching your relations", "function catalog");
            Dictionary<string, List<string>> added = AutoGroupAdds(planes, names, result);
            // A button position (each aircraft uses the relation for its own function) or a relation set to "None" has
            // no function to fill from: no rule and no AI pick for it, never a majority's function (EngineRelationPurpose).
            EngineRelationPurpose.Index purposes = EngineRelationPurpose.Load(result.Warnings);
            HashSet<string> positions = new HashSet<string>(names.Where(n => EngineRelationPurpose.Judge(InternalDataManagement.AllRelations[n], purposes).NoFunction), StringComparer.OrdinalIgnoreCase);
            result.ButtonPositions = positions.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            Dictionary<string, Pair> pairByKey = new Dictionary<string, Pair>(StringComparer.OrdinalIgnoreCase);
            List<Pair> pairs = new List<Pair>();
            foreach (string name in names)
            {
                if (positions.Contains(name))
                    continue;
                Relation relation = InternalDataManagement.AllRelations[name];
                HashSet<string> ticked = Ticked(relation);
                int evidence = ticked.Count(t => t.StartsWith("DCS|", StringComparison.Ordinal));
                foreach (string target in planes)
                {
                    if (ticked.Contains(target))
                        continue;
                    string game = GameOf(target), plane = PlaneOf(target);
                    Pair pair = new Pair { Relation = name, Game = game, Plane = plane, Evidence = game == "DCS" ? evidence : ticked.Count };
                    List<string> specs;
                    if (added.TryGetValue(name, out specs))
                    {
                        foreach (string spec in specs.Where(s => s.StartsWith(game + ":", StringComparison.OrdinalIgnoreCase) && s.EndsWith(":" + plane, StringComparison.OrdinalIgnoreCase)))
                        {
                            string id = spec.Substring(game.Length + 1, spec.Length - game.Length - plane.Length - 2);
                            EngineRelationSuggest.Command command;
                            byKey.TryGetValue(game + "|" + id + "|" + plane, out command);
                            pair.Rules.Add(new EngineFillAction
                            {
                                Spec = spec, Game = game, Id = id, Title = EngineActionInfo.Readable(game, id, command != null ? command.Title : string.Empty), Category = command != null ? command.Category : string.Empty, Plane = plane,
                                IsAxis = relation.ISAXIS, Source = "rules", Confidence = "high", Why = "same function in the manual-checked catalog (Auto Group)", Score = 100
                            });
                        }
                    }
                    if (pair.Rules.Count > 0 || pair.Evidence > 0)
                    {
                        pairs.Add(pair);
                        pairByKey[name + "|" + target] = pair;
                    }
                }
            }
            // Path 1b: Suggest missing for relations that clearly span aircraft but got nothing, within a time cap.
            EngineProgress.Step(1, 2, "Matching your relations", "suggestions");
            Stopwatch suggestWatch = Stopwatch.StartNew();
            List<string> leftovers = pairs.Where(p => p.Rules.Count == 0 && p.Evidence >= 3).Select(p => p.Relation).Distinct().ToList();
            List<string> targetGames = planes.Select(GameOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int suggested = 0;
            foreach (string name in leftovers)
            {
                if (suggestWatch.Elapsed.TotalSeconds > SuggestSeconds)
                {
                    result.Warnings.Add("Suggestions checked " + suggested.ToString(CultureInfo.InvariantCulture) + " of " + leftovers.Count.ToString(CultureInfo.InvariantCulture) +
                                        " relations within " + SuggestSeconds.ToString(CultureInfo.InvariantCulture) + " s.");
                    break;
                }
                suggested++;
                EngineRelationSuggestResult suggest;
                try
                {
                    suggest = EngineRelationSuggest.Suggest(name, "medium", 60, targetGames);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add(name + ": " + ex.Message);
                    continue;
                }
                foreach (string hint in suggest.Hints.Where(h => !result.Warnings.Contains(h)))
                    result.Warnings.Add(name + ": " + hint);
                foreach (EngineRelationSuggestion s in suggest.Suggestions)
                {
                    string game = EngineRelationSuggest.GameKey(s.Game ?? string.Empty);
                    foreach (string plane in game == "DCS" ? s.Planes : new List<string> { game })
                    {
                        Pair pair;
                        if (!pairByKey.TryGetValue(name + "|" + game + "|" + plane, out pair) || pair.Rules.Any(r => r.Id == s.Id))
                            continue;
                        pair.Rules.Add(new EngineFillAction
                        {
                            Spec = game + ":" + s.Id + ":" + plane, Game = game, Id = s.Id, Title = EngineActionInfo.Readable(game, s.Id, s.Title ?? string.Empty), Category = s.Category ?? string.Empty, Plane = plane, IsAxis = s.IsAxis,
                            Source = "rules", Confidence = string.IsNullOrEmpty(s.Confidence) ? "medium" : s.Confidence, Why = s.Why ?? string.Empty, Score = s.Score,
                            Function = s.Function ?? string.Empty
                        });
                    }
                }
            }
            foreach (Pair pair in pairs)
                pair.Rules = pair.Rules.OrderByDescending(a => a.Score).ToList();
            result.RulesMs = watch.ElapsedMilliseconds;

            if (useModel)
                AskModel(pairs, byKey, result);

            // Combine both paths per relation.
            foreach (IGrouping<string, Pair> group in pairs.GroupBy(p => p.Relation))
            {
                EngineFillRelation entry = new EngineFillRelation { Relation = group.Key, IsAxis = InternalDataManagement.AllRelations[group.Key].ISAXIS };
                foreach (Pair pair in group)
                {
                    List<EngineFillAction> picked = Combine(pair);
                    if (picked.Count == 0)
                    {
                        if (pair.Evidence >= 3)
                            entry.NoMatch.Add(pair.Game == "DCS" ? pair.Plane : EngineGameWords.Label(pair.Game));
                        continue;
                    }
                    entry.Actions.AddRange(picked);
                }
                if (entry.Actions.Count > 0 || entry.NoMatch.Count > 0)
                    result.Relations.Add(entry);
            }
            result.RelationsWithActions = result.Relations.Count(r => r.Actions.Any(a => a.Selected));
            result.ActionCount = result.Relations.Sum(r => r.Actions.Count(a => a.Selected));
            result.AmbiguousRelations = result.Relations.Count(r => r.Actions.Any(a => a.Ambiguous));
            result.NoMatchCount = result.Relations.Sum(r => r.NoMatch.Count);
            result.Summary = result.RelationsWithActions.ToString(CultureInfo.InvariantCulture) + " relations get " + result.ActionCount.ToString(CultureInfo.InvariantCulture) +
                             " actions for " + planes.Count.ToString(CultureInfo.InvariantCulture) +
                             (planes.All(p => GameOf(p) == "DCS") ? " aircraft; " : planes.All(p => GameOf(p) != "DCS") ? (planes.Count == 1 ? " game (" + EngineGameWords.Label(GameOf(planes[0])) + "); " : " games; ") : " aircraft and games; ") +
                             result.AmbiguousRelations.ToString(CultureInfo.InvariantCulture) +
                             " relations ambiguous; " + result.NoMatchCount.ToString(CultureInfo.InvariantCulture) + " functions found no match" +
                             (result.ButtonPositions.Count > 0 ? "; " + result.ButtonPositions.Count.ToString(CultureInfo.InvariantCulture) + " button-position relations (different functions per aircraft) left to you" : string.Empty) +
                             (result.Unresolved.Count > 0 ? "; not found: " + string.Join(", ", result.Unresolved) : string.Empty) + ".";
            return result;
        }

        public static int SuggestSeconds = 20;

        private static HashSet<string> Specs(Relation relation)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RelationItem node in (relation.NODES ?? new List<RelationItem>()).Where(n => n != null && !string.IsNullOrWhiteSpace(n.ID)))
            {
                string game = EngineRelationSuggest.GameKey(node.Game ?? string.Empty);
                if (game == "DCS")
                {
                    foreach (string plane in (node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(p => p.Value).Select(p => p.Key))
                        set.Add("DCS:" + node.ID + ":" + plane);
                }
                else if (node.AIRCRAFT == null || node.AIRCRAFT.Count == 0 || node.AIRCRAFT.Values.Any(a => a))
                {
                    set.Add(game + ":" + node.ID + ":" + game);
                }
            }
            return set;
        }

        // What Auto Group would add to these relations for the targets; the profile in memory is restored after.
        private static Dictionary<string, List<string>> AutoGroupAdds(List<string> targets, List<string> names, EngineFillResult result)
        {
            Dictionary<string, List<string>> added = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            Dictionary<string, Relation> relations = new Dictionary<string, Relation>(InternalDataManagement.AllRelations);
            Dictionary<string, Bind> binds = new Dictionary<string, Bind>(InternalDataManagement.AllBinds);
            Dictionary<Relation, List<RelationItem>> nodes = new Dictionary<Relation, List<RelationItem>>();
            Dictionary<RelationItem, Dictionary<string, bool>> ticks = new Dictionary<RelationItem, Dictionary<string, bool>>();
            Dictionary<Relation, List<string>> groups = new Dictionary<Relation, List<string>>();
            Dictionary<string, HashSet<string>> before = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Relation> pair in relations)
            {
                if (pair.Value == null)
                    continue;
                nodes[pair.Value] = pair.Value.NODES == null ? null : pair.Value.NODES.ToList();
                groups[pair.Value] = pair.Value.Groups == null ? null : pair.Value.Groups.ToList();
                foreach (RelationItem node in pair.Value.NODES ?? new List<RelationItem>())
                    if (node != null && !ticks.ContainsKey(node))
                        ticks[node] = node.AIRCRAFT == null ? null : new Dictionary<string, bool>(node.AIRCRAFT);
            }
            foreach (string name in names)
                before[name] = Specs(relations[name]);
            try
            {
                EngineAutoGroupResult group = EngineAutoGroup.Run("all", true, targets.Select(PlaneOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), false, false, true);
                result.Warnings.AddRange(group.Errors.Take(3));
                foreach (string name in names)
                {
                    Relation relation;
                    if (!InternalDataManagement.AllRelations.TryGetValue(name, out relation) || relation == null)
                        continue;
                    List<string> diff = Specs(relation).Where(s => !before[name].Contains(s)).ToList();
                    if (diff.Count > 0)
                        added[name] = diff;
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add("The function catalog match failed: " + ex.Message);
            }
            finally
            {
                InternalDataManagement.AllRelations.Clear();
                foreach (KeyValuePair<string, Relation> pair in relations)
                    InternalDataManagement.AllRelations[pair.Key] = pair.Value;
                InternalDataManagement.AllBinds.Clear();
                foreach (KeyValuePair<string, Bind> pair in binds)
                    InternalDataManagement.AllBinds[pair.Key] = pair.Value;
                foreach (KeyValuePair<Relation, List<RelationItem>> pair in nodes)
                    pair.Key.NODES = pair.Value;
                foreach (KeyValuePair<Relation, List<string>> pair in groups)
                    pair.Key.Groups = pair.Value;
                foreach (KeyValuePair<RelationItem, Dictionary<string, bool>> pair in ticks)
                    pair.Key.AIRCRAFT = pair.Value;
            }
            return added;
        }
        private static HashSet<string> Ticked(Relation relation)
        {
            // "DCS|plane" per ticked DCS aircraft, "GAME|GAME" per other game with an active action.
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RelationItem node in (relation.NODES ?? new List<RelationItem>()).Where(n => n != null))
            {
                string game = EngineRelationSuggest.GameKey(node.Game ?? string.Empty);
                if (game == "DCS")
                {
                    foreach (string plane in (node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(p => p.Value).Select(p => p.Key))
                        set.Add("DCS|" + plane);
                }
                else if (node.AIRCRAFT == null || node.AIRCRAFT.Count == 0 || node.AIRCRAFT.Values.Any(a => a))
                {
                    set.Add(game + "|" + game);
                }
            }
            return set;
        }

        private static List<EngineFillAction> Combine(Pair pair)
        {
            EngineFillAction rule = pair.Rules.FirstOrDefault();
            List<EngineFillAction> list = new List<EngineFillAction>();
            if (!pair.Asked)
            {
                if (rule != null)
                {
                    rule.Selected = rule.Confidence != "low";
                    list.Add(rule);
                }
                return list;
            }
            EngineFillAction ai = pair.Pick == null ? null : pair.Offer.FirstOrDefault(o => o.Id == pair.Pick);
            if (rule != null && ai != null && string.Equals(rule.Id, ai.Id, StringComparison.OrdinalIgnoreCase))
            {
                rule.Source = "both";
                rule.Confidence = "high";
                rule.Why = rule.Why + (pair.Why.Length > 0 ? " | AI: " + pair.Why : string.Empty);
                rule.Selected = true;
                list.Add(rule);
            }
            else if (rule != null && ai != null)
            {
                rule.Ambiguous = ai.Ambiguous = true;
                rule.Selected = ai.Selected = false;
                rule.Confidence = ai.Confidence = "ambiguous";
                ai.Source = "AI";
                ai.Why = pair.Why;
                list.Add(rule);
                list.Add(ai);
            }
            else if (rule != null)
            {
                rule.Confidence = "medium";
                rule.Why = rule.Why + " | AI found no match";
                rule.Selected = true;
                list.Add(rule);
            }
            else if (ai != null)
            {
                // Only the model found it: shown, but unticked until the user agrees.
                ai.Source = "AI";
                ai.Confidence = "medium";
                ai.Why = pair.Why;
                ai.Selected = false;
                list.Add(ai);
            }
            return list;
        }

        // ------------------------------------------------------------------------------------------------------
        // The model path
        // ------------------------------------------------------------------------------------------------------

        private static void AskModel(List<Pair> pairs, Dictionary<string, EngineRelationSuggest.Command> byKey, EngineFillResult result)
        {
            EngineLlm.ModelInfo model;
            string problem;
            if (!EngineLlm.TryGetUsableModel(out model, out problem))
            {
                result.Note = "Rules only: " + problem;
                return;
            }
            // The likeliest misses first: no rule pick although the relation has many aircraft, then uncertain picks;
            // confident rule picks need no second opinion.
            // --llm on: a second opinion on every pair.
            List<Pair> queue = pairs.Where(p => CheckAll || (p.Rules.Count == 0 ? p.Evidence >= 2 : p.Rules[0].Confidence != "high"))
                .OrderBy(p => p.Rules.Count == 0 ? 0 : 1).ThenByDescending(p => p.Evidence).ToList();
            if (queue.Count == 0)
                return;
            Dictionary<string, string> cache = LoadCache();
            string modelKey = model.Id;
            foreach (Pair pair in queue)
            {
                pair.Offer = Offer(pair, byKey);
                string cached;
                if (cache.TryGetValue(CacheKey(pair, modelKey), out cached))
                {
                    pair.Asked = true;
                    int bar = cached.IndexOf('|');
                    string pick = bar < 0 ? cached : cached.Substring(0, bar);
                    pair.Pick = pick == "none" ? null : pick;
                    pair.Why = bar < 0 ? string.Empty : cached.Substring(bar + 1);
                    result.CachedPairs++;
                }
            }
            List<Pair> open = queue.Where(p => !p.Asked && p.Offer.Count > 0).ToList();
            if (open.Count == 0)
                return;
            Stopwatch watch = Stopwatch.StartNew();
            result.Model = model.Id;
            EngineLlm.Session session = EngineLlm.Session.Start(model, model.Local ? Math.Max(model.ContextSize, 8192) : model.ContextSize);
            try
            {
                if (session.Error.Length > 0)
                {
                    result.Note = "Rules only: the local AI model did not start (" + session.Error + ").";
                    return;
                }
                for (int start = 0; start < open.Count; start += BatchSize)
                {
                    if (watch.Elapsed.TotalSeconds > ModelSeconds)
                    {
                        result.Note = "The AI checked " + result.AskedPairs.ToString(CultureInfo.InvariantCulture) + " of " + open.Count.ToString(CultureInfo.InvariantCulture) +
                                      " relation x aircraft pairs within " + ModelSeconds.ToString(CultureInfo.InvariantCulture) + " s; the rest are rules only.";
                        break;
                    }
                    List<Pair> batch = open.Skip(start).Take(BatchSize).ToList();
                    EngineProgress.Step(start, open.Count, "The AI checks the matches", batch[0].Relation + " / " + batch[0].Plane);
                    EngineLlm.CallResult call = session.Ask(Messages(batch, byKey), Grammar(), 160 * batch.Count, false);
                    if (!string.IsNullOrEmpty(call.Error))
                    {
                        result.Warnings.Add("The local AI model stopped answering (" + call.Error + ").");
                        break;
                    }
                    ReadPicks(call.Content, batch, result);
                    foreach (Pair pair in batch.Where(p => p.Asked))
                        cache[CacheKey(pair, modelKey)] = (pair.Pick ?? "none") + "|" + pair.Why;
                }
            }
            finally
            {
                session.Dispose();
                result.ModelMs = watch.ElapsedMilliseconds;
                SaveCache(cache);
            }
        }

        // The aircraft's candidates: the rule picks, then a catalog search with the relation's name and action titles.
        // The prompt the model sees for one relation x target ("GAME|plane"), for the AI matrix (relations ai-matrix).
        internal static string ModelPrompt(string relation, string target)
        {
            Dictionary<string, EngineRelationSuggest.Command> byKey = new Dictionary<string, EngineRelationSuggest.Command>(StringComparer.OrdinalIgnoreCase);
            foreach (EngineRelationSuggest.Command command in EngineRelationSuggest.Index(new List<string>()))
            {
                string key = command.Game + "|" + command.Id + "|" + command.Plane;
                if (!byKey.ContainsKey(key))
                    byKey[key] = command;
            }
            Pair pair = new Pair { Relation = relation, Game = GameOf(target), Plane = PlaneOf(target) };
            pair.Offer = Offer(pair, byKey);
            return string.Join(Environment.NewLine, Messages(new List<Pair> { pair }, byKey).Select(m => m.Content));
        }

        private static List<EngineFillAction> Offer(Pair pair, Dictionary<string, EngineRelationSuggest.Command> byKey)
        {
            Relation relation = InternalDataManagement.AllRelations[pair.Relation];
            List<EngineFillAction> offer = pair.Rules.Take(4).Select(r => new EngineFillAction
            {
                Spec = r.Spec, Game = r.Game, Id = r.Id, Title = r.Title, Category = r.Category, Plane = r.Plane, IsAxis = r.IsAxis, Source = "AI", Confidence = "medium", Why = string.Empty,
                Function = r.Function
            }).ToList();
            List<string> queries = new List<string> { pair.Relation };
            queries.AddRange(Examples(relation, byKey).Select(e => e.Title).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(2));
            foreach (string query in queries)
            {
                if (offer.Count >= Candidates)
                    break;
                EngineRelationSearchResult search;
                try
                {
                    search = EngineRelationSearch.Search(query, new List<string> { pair.Game }, pair.Game == "DCS" ? new List<string> { pair.Plane } : null, relation.ISAXIS, 150);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (EngineRelationSearchHit hit in search.Results)
                {
                    if (offer.Count >= Candidates)
                        break;
                    // Only what exists in that aircraft (the search ranks every aircraft).
                    if (pair.Game == "DCS" && (hit.Planes == null || !hit.Planes.Contains(pair.Plane, StringComparer.OrdinalIgnoreCase)))
                        continue;
                    if (offer.Any(o => string.Equals(o.Id, hit.Id, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    offer.Add(new EngineFillAction
                    {
                        Spec = pair.Game + ":" + hit.Id + ":" + pair.Plane, Game = pair.Game, Id = hit.Id, Title = EngineActionInfo.Readable(pair.Game, hit.Id, hit.Title ?? string.Empty), Category = hit.Category ?? string.Empty, Plane = pair.Plane,
                        IsAxis = hit.IsAxis, Source = "AI", Confidence = "medium", Function = hit.Function ?? string.Empty
                    });
                }
            }
            return offer;
        }

        private static List<EngineRelationSuggest.Command> Examples(Relation relation, Dictionary<string, EngineRelationSuggest.Command> byKey)
        {
            List<EngineRelationSuggest.Command> examples = new List<EngineRelationSuggest.Command>();
            foreach (RelationItem node in (relation.NODES ?? new List<RelationItem>()).Where(n => n != null && n.AIRCRAFT != null))
            {
                string game = EngineRelationSuggest.GameKey(node.Game ?? string.Empty);
                IEnumerable<string> active = game == "DCS" ? node.AIRCRAFT.Where(p => p.Value).Select(p => p.Key).Take(2) : new[] { game };
                foreach (string plane in active)
                {
                    EngineRelationSuggest.Command command;
                    if (byKey.TryGetValue(game + "|" + node.ID + "|" + plane, out command) || (game != "DCS" && (command = EngineIdMeanings.Find(game, node.ID, plane)) != null))
                        examples.Add(command);
                }
                if (examples.Count >= 5)
                    break;
            }
            return examples;
        }

        private const string FillSystem =
            "You check which action of an aircraft or a game belongs into a JoyPro relation. A relation groups the same function of many aircraft and games " +
            "(its name and its current actions show the purpose). The target is a DCS aircraft or a whole game (MSFS, X-Plane, IL-2, War Thunder, Falcon BMS, " +
            "Star Citizen, Elite Dangerous, X4 and more; Star Citizen, Elite Dangerous and X4 fly ships). For every PAIR pick the one candidate that does the same thing in the target, " +
            "or \"none\" when no candidate fits. Same function and direction matter (gear up is not gear down, a toggle is not an on/off pair); " +
            "axes go to axis relations only. Answer JSON only: {\"picks\":[{\"pair\":1,\"pick\":\"c2\",\"why\":\"short reason\"}]}.";

        private static List<EngineLlm.Message> Messages(List<Pair> batch, Dictionary<string, EngineRelationSuggest.Command> byKey)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < batch.Count; i++)
            {
                Pair pair = batch[i];
                Relation relation = InternalDataManagement.AllRelations[pair.Relation];
                sb.Append("PAIR ").Append(i + 1).Append(": relation \"").Append(pair.Relation).Append("\" (").Append(relation.ISAXIS ? "axis" : "button").Append(")\n");
                foreach (EngineRelationSuggest.Command example in Examples(relation, byKey).GroupBy(e => e.Title + "|" + e.Category).Select(g => g.First()).Take(4))
                    sb.Append("  has: ").Append(Record(example.Game, example.Plane, example.Id, example.Title, example.Category, example.IsAxis, string.Empty)).Append('\n');
                sb.Append("  target: ").Append(pair.Game == "DCS" ? "DCS " + pair.Plane : EngineRelationAssistantGameNames.Label(pair.Game)).Append('\n');
                for (int c = 0; c < pair.Offer.Count; c++)
                {
                    EngineFillAction offer = pair.Offer[c];
                    sb.Append("  c").Append(c + 1).Append(": ").Append(Record(offer.Game, offer.Plane, offer.Id, offer.Title, offer.Category, offer.IsAxis, offer.Function)).Append('\n');
                }
            }
            return new List<EngineLlm.Message>
            {
                new EngineLlm.Message { Role = "system", Content = FillSystem },
                new EngineLlm.Message { Role = "user", Content = sb.ToString() }
            };
        }

        // One action as the model sees it: what it is in that cockpit, from JoyPro's own data (the cockpit guide's control
        // text, the sim's description, the function's general explanation), never guessed: "no manual data" otherwise.
        //   "Speed Brake Switch - Aft [Throttle Quadrant] button | slot airbrake | does: Extends the speed brake (guide)"
        internal static string Record(string game, string plane, string id, string title, string category, bool isAxis, string function)
        {
            string g = EngineRelationSuggest.GameKey(game ?? string.Empty);
            EngineActionInfo.Info info = EngineActionInfo.Describe(g, g == "DCS" ? plane : g, id, title, function, string.Empty);
            StringBuilder sb = new StringBuilder(info.Readable.Length > 0 ? info.Readable : title ?? id);
            if (!string.IsNullOrEmpty(category))
                sb.Append(" [").Append(category).Append("]");
            sb.Append(isAxis ? " axis" : " button");
            if (!string.IsNullOrEmpty(function))
                sb.Append(" | slot ").Append(function);
            string does = EngineActionInfo.InGameWords(g, category, Regex.Replace(info.Explanation ?? string.Empty, @"\s+", " ").Trim());
            if (does.Length > 140)
                does = does.Substring(0, 137) + "...";
            sb.Append(" | does: ").Append(does.Length > 0 ? does + " (" + info.Source + ")" : "no manual data");
            if (g != "DCS" && plane != g)
                sb.Append(" | ").Append(plane);
            return sb.ToString();
        }

        private static string Grammar()
        {
            return string.Join("\n", new[]
            {
                "root ::= \"{\" ws \"\\\"picks\\\"\" ws \":\" ws \"[\" ws ( pick ( ws \",\" ws pick )* )? ws \"]\" ws \"}\"",
                "pick ::= \"{\" ws \"\\\"pair\\\"\" ws \":\" ws [1-9] ws \",\" ws \"\\\"pick\\\"\" ws \":\" ws choice ws \",\" ws \"\\\"why\\\"\" ws \":\" ws str ws \"}\"",
                "choice ::= \"\\\"none\\\"\" | \"\\\"c\" [1-9] \"\\\"\"",
                "str ::= \"\\\"\" ( [^\"\\\\\\x00-\\x1f] | \"\\\\\" [\"\\\\/nt] )* \"\\\"\"",
                "ws ::= [ \\n]?"
            });
        }

        private static void ReadPicks(string content, List<Pair> batch, EngineFillResult result)
        {
            Dictionary<string, object> answer;
            try
            {
                answer = EngineSerialization.FromJson<Dictionary<string, object>>(content ?? string.Empty);
            }
            catch
            {
                result.Warnings.Add("The local AI model gave no usable answer for " + batch.Count.ToString(CultureInfo.InvariantCulture) + " pairs.");
                return;
            }
            foreach (Dictionary<string, object> item in EngineNlGroup.Objects(EngineNlGroup.Get(answer, "picks")))
            {
                int index;
                if (!int.TryParse(Convert.ToString(EngineNlGroup.Get(item, "pair"), CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 1 || index > batch.Count)
                    continue;
                Pair pair = batch[index - 1];
                string pick = EngineNlGroup.Text(item, "pick").Trim();
                int c;
                pair.Asked = true;
                pair.Why = EngineNlGroup.Text(item, "why").Trim();
                pair.Pick = pick.StartsWith("c", StringComparison.OrdinalIgnoreCase) && int.TryParse(pick.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out c) && c >= 1 && c <= pair.Offer.Count
                    ? pair.Offer[c - 1].Id
                    : null;
                result.AskedPairs++;
            }
        }

        private static string CacheFile() { return Path.Combine(EngineLlm.DataRoot(), "fill-cache.json"); }

        private static string CacheKey(Pair pair, string model)
        {
            Relation relation = InternalDataManagement.AllRelations[pair.Relation];
            return model + "|" + pair.Relation + "|" + pair.Plane + "|" + (relation.NODES == null ? 0 : relation.NODES.Count).ToString(CultureInfo.InvariantCulture) + "|" +
                   string.Join(",", pair.Offer.Select(o => o.Id));
        }

        private static Dictionary<string, string> LoadCache()
        {
            try
            {
                if (File.Exists(CacheFile()))
                    return EngineSerialization.FromJson<Dictionary<string, string>>(File.ReadAllText(CacheFile(), Encoding.UTF8)) ?? new Dictionary<string, string>();
            }
            catch
            {
            }
            return new Dictionary<string, string>();
        }

        private static void SaveCache(Dictionary<string, string> cache)
        {
            try
            {
                Directory.CreateDirectory(EngineLlm.DataRoot());
                // Small: the newest few thousand picks.
                Dictionary<string, string> keep = cache.Skip(Math.Max(0, cache.Count - 5000)).ToDictionary(p => p.Key, p => p.Value);
                File.WriteAllText(CacheFile(), EngineSerialization.ToJson(keep), new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }

    public sealed class EngineFillAction
    {
        public string Spec { get; set; }
        public string Game { get; set; }
        public string Function { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public string Category { get; set; }
        public string Plane { get; set; }
        public bool IsAxis { get; set; }
        // rules, AI or both
        public string Source { get; set; }
        // high, medium, low or ambiguous
        public string Confidence { get; set; }
        public string Why { get; set; }
        public int Score { get; set; }
        public bool Selected { get; set; }
        public bool Ambiguous { get; set; }

        public EngineFillAction()
        {
            Spec = Game = Function = Id = Title = Category = Plane = Source = Confidence = Why = string.Empty;
        }
    }

    public sealed class EngineFillRelation
    {
        public string Relation { get; set; }
        public bool IsAxis { get; set; }
        public List<EngineFillAction> Actions { get; set; }
        // Aircraft the relation should have (it has many others) but neither path found.
        public List<string> NoMatch { get; set; }

        public EngineFillRelation()
        {
            Relation = string.Empty;
            Actions = new List<EngineFillAction>();
            NoMatch = new List<string>();
        }
    }

    public sealed class EngineFillResult
    {
        public string Summary { get; set; }
        public List<string> Aircraft { get; set; }
        public List<string> Unresolved { get; set; }
        public int RelationsWithActions { get; set; }
        public int ActionCount { get; set; }
        public int AmbiguousRelations { get; set; }
        public int NoMatchCount { get; set; }
        public string Model { get; set; }
        public int AskedPairs { get; set; }
        public int CachedPairs { get; set; }
        public long RulesMs { get; set; }
        public long ModelMs { get; set; }
        public string Note { get; set; }
        // Relations without a function to fill from (button positions, or set to "None"): left out.
        public List<string> ButtonPositions { get; set; }
        public List<EngineFillRelation> Relations { get; set; }
        public List<string> Warnings { get; set; }

        public EngineFillResult()
        {
            Summary = string.Empty;
            Model = string.Empty;
            Note = string.Empty;
            Aircraft = new List<string>();
            Unresolved = new List<string>();
            Relations = new List<EngineFillRelation>();
            ButtonPositions = new List<string>();
            Warnings = new List<string>();
        }
    }
}
