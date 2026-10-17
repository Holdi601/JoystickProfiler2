using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    // "Auto Add to Relation": reads a relation's name and its elements and proposes what is still missing, each with the
    // reason. Three sources, strongest first:
    //   catalog  - the relation stands for a function of the manual-verified function catalog (DB/Functions), and the
    //              catalog lists that function for more aircraft / games;
    //   same id  - a DCS command of the relation exists with the same id in aircraft the relation does not cover yet;
    //   title    - commands of any game whose title matches the relation's titles (never the opposite direction).
    // Planes outside the relation's Auto Group split ("[Fixed wing]", "[Rotary wing, WW2]", per aircraft) are not
    // proposed. A command another relation already uses is still shown, with that relation named. "Never" adds the
    // command switched off, so it is not proposed again (and Auto Group keeps it off too).
    internal static class EngineRelationSuggest
    {
        internal sealed class Command
        {
            public string Game = string.Empty;
            public string Plane = string.Empty;
            public string Id = string.Empty;
            public string Title = string.Empty;
            public string Category = string.Empty;
            public bool IsAxis;
        }

        private sealed class Candidate
        {
            public string Game = string.Empty;
            public string Id = string.Empty;
            public string Title = string.Empty;
            public string Category = string.Empty;
            public bool IsAxis;
            public int Score;
            public string Source = string.Empty;
            public readonly SortedSet<string> Planes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            // Collected per source, written as one sentence each (all aircraft of a source in one line).
            public readonly SortedSet<string> CatalogPlanes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly SortedSet<string> SubstitutePlanes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly SortedSet<string> SameIdPlanes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly SortedSet<string> HabitPlanes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            public string SlotName = string.Empty;
            public string SameIdNote = string.Empty;
            public string TitleReference = string.Empty;
            public int TitleScore;
            // The relation's own entry that has the same meaning ("A-10C 'HOTAS CMS Forward'"), for the short reason.
            public string Reference = string.Empty;
            public string HabitFrom = string.Empty;
            public int HabitCount;
            // The function map (EngineFunctionMap): the function in plain words ("Throttle axis"), the relation's own
            // entry that is it, the word that decided it for this command.
            public readonly SortedSet<string> MapPlanes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            public string MapFunction = string.Empty;
            public string MapFunctionId = string.Empty;
            public string MapReference = string.Empty;
            public string MapTerm = string.Empty;

            public List<string> Reasons()
            {
                List<string> reasons = new List<string>();
                string same = Reference.Length > 0 ? "Same function as " + Reference + ": " : string.Empty;
                if (CatalogPlanes.Count > 0)
                    reasons.Add(same + "'" + Title + "' is the '" + SlotName + "' function in " + Join(CatalogPlanes) + " (function catalog, checked against the manuals).");
                if (MapPlanes.Count > 0)
                    reasons.Add("Same function: " + MapFunction + (MapReference.Length > 0 ? ", like " + MapReference : string.Empty) + "; '" + Title + "' is it in " + Join(MapPlanes) +
                                (MapTerm.Length > 0 && !MapTerm.StartsWith("slot ", StringComparison.Ordinal) ? " ('" + MapTerm + "', JoyPro function map)." : " (JoyPro function map)."));
                if (HabitPlanes.Count > 0)
                    reasons.Add("Your style: where an aircraft has no '" + SlotName + "', you use its '" + HabitFrom + "' (" + HabitCount.ToString(CultureInfo.InvariantCulture) +
                                " times in your relations); " + Join(HabitPlanes) + ".");
                if (SubstitutePlanes.Count > 0)
                    reasons.Add("Closest available function to '" + SlotName + "' in " + Join(SubstitutePlanes) + " (catalog substitute, the aircraft has no exact one).");
                if (SameIdPlanes.Count > 0)
                    reasons.Add("Same DCS command id in " + Join(SameIdPlanes) + SameIdNote);
                if (TitleReference.Length > 0)
                    reasons.Add("Title matches '" + TitleReference + "' (" + TitleScore.ToString(CultureInfo.InvariantCulture) + "% of the words).");
                return reasons;
            }

            // One line for a list row: why it fits, strongest source first.
            public string Short()
            {
                if (CatalogPlanes.Count > 0)
                    return Reference.Length > 0 ? "Same function as " + Reference : "Function '" + SlotName + "' (catalog)";
                if (HabitPlanes.Count > 0)
                    return "Your style: '" + HabitFrom + "' where '" + SlotName + "' is missing";
                if (MapPlanes.Count > 0 && Source == "function map")
                    return "Same function: " + MapFunction;
                if (SameIdPlanes.Count > 0)
                    return "Same command id" + SameIdNote.TrimEnd('.');
                if (MapPlanes.Count > 0)
                    return "Same function: " + MapFunction;
                if (SubstitutePlanes.Count > 0)
                    return "Closest to '" + SlotName + "' (catalog substitute)";
                return TitleReference.Length > 0 ? "Title match: '" + TitleReference + "' (" + TitleScore.ToString(CultureInfo.InvariantCulture) + "%)" : Source;
            }

            private static string Join(IEnumerable<string> planes)
            {
                List<string> list = planes.ToList();
                return list.Count <= 6 ? string.Join(", ", list) : string.Join(", ", list.Take(5)) + " and " + (list.Count - 5).ToString(CultureInfo.InvariantCulture) + " more";
            }
        }

        // A function the relation stands for: from its name (weight 1) or from its commands (weight = how many agree).
        internal sealed class Meaning
        {
            public EngineAutoGroup.Slot Slot;
            public double Weight;
            public string Reference = string.Empty;
        }

        // The same in the function map (any game, any aircraft, with a direction).
        internal sealed class MapMeaning
        {
            public EngineFunctionMap.Function Function;
            public string Dir = string.Empty;
            public double Weight;
            public string Reference = string.Empty;
        }

        private static List<Command> index;

        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "to", "and", "or", "for", "on", "in", "with", "by", "at", "is", "button", "switch", "key", "toggle", "dcs", "space"
        };

        public static EngineRelationSuggestResult Suggest(string relationName, string minConfidence, int limit)
        {
            return Suggest(relationName, minConfidence, limit, null);
        }

        // games: only these games (GameKey; "DCS" for every DCS aircraft); null or empty = all.
        public static EngineRelationSuggestResult Suggest(string relationName, string minConfidence, int limit, ICollection<string> games)
        {
            EngineRelationSuggestResult result = new EngineRelationSuggestResult { Relation = relationName ?? string.Empty };
            Relation relation;
            if (!InternalDataManagement.AllRelations.TryGetValue(relationName ?? string.Empty, out relation) || relation == null)
            {
                result.Errors.Add("Relation not found: " + relationName);
                return result;
            }
            Context context = Context.Load(result.Warnings);
            List<Candidate> candidates = Build(relation, context, true);
            result.Slot = context.LastSlot == null ? string.Empty : context.LastSlot.Id;
            result.SlotName = context.LastSlot == null ? string.Empty : context.LastSlot.Name;
            result.IsAxis = relation.ISAXIS;
            result.Functions.AddRange(context.LastMeanings.Select(m => new EngineRelationFunction { Slot = m.Slot.Id, Name = m.Slot.Name, Weight = Math.Round(m.Weight, 2), Reference = m.Reference }));
            result.MapFunctions.AddRange(context.LastMapMeanings.Select(m => new EngineRelationMapFunction { Id = m.Function.Id, Name = m.Function.Name, Dir = m.Dir, Weight = Math.Round(m.Weight, 2), Reference = m.Reference }));
            result.Misfits.AddRange(Misfits(relation, context, context.LastSlot));
            result.Dismissed.AddRange(Dismissed(relation, context));
            HashSet<string> gameFilter = new HashSet<string>((games ?? new List<string>()).Select(GameKey).Where(g => g.Length > 0), StringComparer.OrdinalIgnoreCase);
            int min = Rank(minConfidence);
            Coverage coverage = Coverage.Of(relation);
            foreach (Candidate candidate in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Game, StringComparer.Ordinal).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase))
            {
                string confidence = Confidence(candidate.Score);
                if (Rank(confidence) < min)
                    continue;
                if (gameFilter.Count > 0 && !gameFilter.Contains(candidate.Game))
                    continue;
                if (limit > 0 && result.Suggestions.Count >= limit)
                    break;
                int near = 0;
                int different = 0;
                List<EngineAircraftVariant> variants = candidate.Game == "DCS"
                    ? EngineIdMeanings.Describe(candidate.Game, candidate.Id, candidate.Planes, out near, out different)
                    : new List<EngineAircraftVariant>();
                result.Suggestions.Add(new EngineRelationSuggestion
                {
                    Variants = variants,
                    Near = near,
                    SameIdDifferent = different,
                    MeaningNote = EngineRelationSearch.MeaningNote(near, different),
                    Game = candidate.Game,
                    Id = candidate.Id,
                    Title = candidate.Title,
                    Category = candidate.Category,
                    IsAxis = candidate.IsAxis,
                    Planes = candidate.Planes.ToList(),
                    Confidence = confidence,
                    Score = candidate.Score,
                    Source = candidate.Source,
                    Why = candidate.Short(),
                    Reasons = candidate.Reasons(),
                    InOtherRelations = context.OtherRelations(candidate.Game, candidate.Id, candidate.Planes, relation.NAME),
                    Function = candidate.MapFunctionId
                });
                MarkMissing(result.Suggestions[result.Suggestions.Count - 1], coverage);
            }
            return result;
        }

        // What the relation covers: the games and the (game, aircraft) its switched-on actions are for ("DCS|A-10C",
        // "IL2Game|IL2Game"). Switched-off aircraft (dismissed suggestions) do not count.
        internal sealed class Coverage
        {
            public readonly HashSet<string> Games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Planes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public static Coverage Of(Relation relation)
            {
                Coverage coverage = new Coverage();
                foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
                {
                    if (node == null || string.IsNullOrWhiteSpace(node.ID))
                        continue;
                    string game = GameKey(node.Game ?? string.Empty);
                    Dictionary<string, bool> aircraft = node.AIRCRAFT ?? new Dictionary<string, bool>();
                    if (game != "DCS")
                    {
                        if (aircraft.Count == 0 || aircraft.Any(p => p.Value))
                        {
                            coverage.Games.Add(game);
                            coverage.Planes.Add(game + "|" + game);
                        }
                        continue;
                    }
                    foreach (KeyValuePair<string, bool> plane in aircraft.Where(p => p.Value))
                    {
                        coverage.Games.Add(game);
                        coverage.Planes.Add(game + "|" + plane.Key);
                    }
                }
                return coverage;
            }
        }

        // "Suggest missing": a suggestion for a game the relation has no action of yet (NewGame), or for aircraft it has
        // none for (NewPlanes); else it is one more action for aircraft the relation already covers.
        internal static void MarkMissing(EngineRelationSuggestion suggestion, Coverage coverage)
        {
            string game = suggestion.Game ?? string.Empty;
            suggestion.NewGame = !coverage.Games.Contains(game);
            List<string> planes = game == "DCS" ? suggestion.Planes : new List<string> { game };
            suggestion.NewPlanes = planes.Where(p => !coverage.Planes.Contains(game + "|" + p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            suggestion.Missing = suggestion.NewGame ? "game" : suggestion.NewPlanes.Count > 0 ? "aircraft" : string.Empty;
        }

        // relations suggest --all --missing: per relation what it lacks (games and aircraft it has no action for yet),
        // only actions no other relation uses; the ones another relation uses are counted (UsedElsewhere). The fast
        // sources only (function catalog, same id, your style), like the summary.
        public static EngineRelationSuggestResult Missing(string minConfidence, int perRelation)
        {
            EngineRelationSuggestResult result = new EngineRelationSuggestResult();
            Context context = Context.Load(result.Warnings);
            int min = Rank(minConfidence);
            foreach (Relation relation in InternalDataManagement.AllRelations.Values.Where(r => r != null && r.NODES != null && r.NODES.Count > 0)
                         .OrderBy(r => r.NAME, StringComparer.OrdinalIgnoreCase))
            {
                Coverage coverage = Coverage.Of(relation);
                List<EngineRelationSuggestion> free = new List<EngineRelationSuggestion>();
                int elsewhere = 0;
                foreach (Candidate candidate in Build(relation, context, false).Where(c => Rank(Confidence(c.Score)) >= min))
                {
                    EngineRelationSuggestion suggestion = new EngineRelationSuggestion
                    {
                        Game = candidate.Game,
                        Id = candidate.Id,
                        Title = candidate.Title,
                        Category = candidate.Category,
                        IsAxis = candidate.IsAxis,
                        Planes = candidate.Planes.ToList(),
                        Confidence = Confidence(candidate.Score),
                        Score = candidate.Score,
                        Source = candidate.Source,
                        Why = candidate.Short(),
                        InOtherRelations = context.OtherRelations(candidate.Game, candidate.Id, candidate.Planes, relation.NAME),
                        Function = candidate.MapFunctionId
                    };
                    MarkMissing(suggestion, coverage);
                    if (suggestion.Missing.Length == 0)
                        continue;
                    if (suggestion.InOtherRelations.Count > 0)
                    {
                        elsewhere++;
                        continue;
                    }
                    free.Add(suggestion);
                }
                if (free.Count == 0 && elsewhere == 0)
                    continue;
                List<EngineRelationSuggestion> ordered = free.OrderByDescending(s => s.NewGame).ThenByDescending(s => s.Score).ThenByDescending(s => s.NewPlanes.Count)
                    .ThenBy(s => s.Game, StringComparer.Ordinal).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
                result.Relations.Add(new EngineRelationSuggestSummary
                {
                    Relation = relation.NAME,
                    IsAxis = relation.ISAXIS,
                    Suggestions = free.Count,
                    High = free.Count(s => s.Confidence == "high"),
                    Planes = free.Sum(s => s.NewPlanes.Count),
                    NewGames = free.Where(s => s.NewGame).Select(s => s.Game).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    UsedElsewhere = elsewhere,
                    Items = perRelation > 0 ? ordered.Take(perRelation).ToList() : ordered
                });
            }
            return result;
        }

        // What the user dismissed in this relation ("Never": the command is in it switched off), so it can be restored.
        private static List<EngineRelationDismissed> Dismissed(Relation relation, Context context)
        {
            List<EngineRelationDismissed> list = new List<EngineRelationDismissed>();
            foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
            {
                if (node == null || string.IsNullOrWhiteSpace(node.ID) || node.AIRCRAFT == null)
                    continue;
                string game = GameKey(node.Game);
                List<string> off = node.AIRCRAFT.Where(p => !p.Value).Select(p => p.Key).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                if (off.Count == 0)
                    continue;
                Command command = context.Find(game, node.ID, off[0]);
                list.Add(new EngineRelationDismissed { Game = game, Id = node.ID, Title = command != null ? command.Title : node.ID, Planes = off });
            }
            return list;
        }

        // Every relation with something to add (catalog and same-id sources only: fast enough for the whole profile).
        public static EngineRelationSuggestResult Summary(string minConfidence)
        {
            EngineRelationSuggestResult result = new EngineRelationSuggestResult();
            Context context = Context.Load(result.Warnings);
            int min = Rank(minConfidence);
            foreach (Relation relation in InternalDataManagement.AllRelations.Values.Where(r => r != null && r.NODES != null && r.NODES.Count > 0)
                         .OrderBy(r => r.NAME, StringComparer.OrdinalIgnoreCase))
            {
                List<Candidate> candidates = Build(relation, context, false).Where(c => Rank(Confidence(c.Score)) >= min).ToList();
                int misfits = Misfits(relation, context, context.LastSlot).Count;
                if (candidates.Count == 0 && misfits == 0)
                    continue;
                result.Relations.Add(new EngineRelationSuggestSummary
                {
                    Relation = relation.NAME,
                    Suggestions = candidates.Count,
                    High = candidates.Count(c => Confidence(c.Score) == "high"),
                    Planes = candidates.Sum(c => c.Planes.Count),
                    Misfits = misfits
                });
            }
            return result;
        }

        // Applies the user's decisions: "add" nodes are added switched on, "never" nodes switched off (remembered).
        public static EngineRelationSuggestApplyResult Apply(string relationName, List<string> add, List<string> never)
        {
            return Apply(relationName, add, never, new List<string>(), new List<string>());
        }

        // remove: "game:id:planes" leaves the relation; move: "game:id:planes=>Target relation" goes to that relation.
        public static EngineRelationSuggestApplyResult Apply(string relationName, List<string> add, List<string> never, List<string> remove, List<string> move)
        {
            return Apply(relationName, add, never, remove, move, new List<string>());
        }

        // take: "game:id:planes" comes out of every other relation that uses it and into this one (the conflict a
        // suggestion shows as "already in 'Flare'").
        public static EngineRelationSuggestApplyResult Apply(string relationName, List<string> add, List<string> never, List<string> remove, List<string> move, List<string> take)
        {
            EngineRelationSuggestApplyResult result = new EngineRelationSuggestApplyResult { Relation = relationName ?? string.Empty };
            Relation relation;
            if (!InternalDataManagement.AllRelations.TryGetValue(relationName ?? string.Empty, out relation) || relation == null)
            {
                result.Errors.Add("Relation not found: " + relationName);
                return result;
            }
            List<string> takeAdds = new List<string>();
            foreach (string spec in take ?? new List<string>())
            {
                string game;
                string id;
                List<string> planes;
                if (!TryParseNode(spec, out game, out id, out planes))
                {
                    result.Errors.Add("Not a node (game:id:plane1|plane2): " + spec);
                    continue;
                }
                bool? axis = Program.TryInferNodeAxis(game, id);
                if (axis.HasValue && axis.Value != relation.ISAXIS)
                {
                    result.Errors.Add(game + ":" + id + " is " + (axis.Value ? "an axis" : "a button") + "; the relation is " + (relation.ISAXIS ? "an axis." : "a button."));
                    continue;
                }
                foreach (Relation other in InternalDataManagement.AllRelations.Values.Where(r => r != null && r != relation && r.NODES != null).ToList())
                {
                    RelationItem node = other.NODES.FirstOrDefault(n => n != null && string.Equals(n.ID, id, StringComparison.OrdinalIgnoreCase) &&
                                                                   string.Equals(GameKey(n.Game), GameKey(game), StringComparison.OrdinalIgnoreCase));
                    if (node == null || node.AIRCRAFT == null)
                        continue;
                    bool touched = false;
                    foreach (string plane in planes.Where(p => node.AIRCRAFT.ContainsKey(p) && node.AIRCRAFT[p]))
                    {
                        node.DeleteAircraftFromActivity(plane);
                        touched = true;
                        result.Taken++;
                        if (!result.TakenFrom.Contains(other.NAME))
                            result.TakenFrom.Add(other.NAME);
                    }
                    if (touched && (node.AIRCRAFT.Count == 0 || GameKey(node.Game) != "DCS"))
                        other.NODES.Remove(node);
                }
                takeAdds.Add(spec);
            }
            add = (add ?? new List<string>()).Concat(takeAdds).ToList();
            never = never ?? new List<string>();
            remove = remove ?? new List<string>();
            move = move ?? new List<string>();
            foreach (string spec in remove.Concat(move))
            {
                string node = spec;
                Relation target = null;
                int arrow = spec.LastIndexOf("=>", StringComparison.Ordinal);
                if (move.Contains(spec))
                {
                    if (arrow <= 0 || !InternalDataManagement.AllRelations.TryGetValue(spec.Substring(arrow + 2).Trim(), out target) || target == null)
                    {
                        result.Errors.Add("Move needs game:id:planes=>existing relation: " + spec);
                        continue;
                    }
                    node = spec.Substring(0, arrow);
                    if (target.ISAXIS != relation.ISAXIS)
                    {
                        result.Errors.Add("'" + target.NAME + "' is " + (target.ISAXIS ? "an axis" : "a button") + " relation; " + node + " stays.");
                        continue;
                    }
                }
                string game;
                string id;
                List<string> planes;
                if (!TryParseNode(node, out game, out id, out planes))
                {
                    result.Errors.Add("Not a node (game:id:plane1|plane2): " + node);
                    continue;
                }
                RelationItem existing = (relation.NODES ?? new List<RelationItem>()).FirstOrDefault(n => n != null && string.Equals(n.ID, id, StringComparison.OrdinalIgnoreCase) &&
                                                                                                 string.Equals(GameKey(n.Game), GameKey(game), StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    result.Errors.Add(game + ":" + id + " is not in '" + relation.NAME + "'.");
                    continue;
                }
                foreach (string plane in planes)
                {
                    existing.DeleteAircraftFromActivity(plane);
                    if (target != null)
                    {
                        target.AddNode(id, game, target.ISAXIS, plane);
                        RelationItem moved = target.NODES.FirstOrDefault(n => n != null && string.Equals(n.ID, id, StringComparison.OrdinalIgnoreCase) &&
                                                                            string.Equals(GameKey(n.Game), GameKey(game), StringComparison.OrdinalIgnoreCase));
                        if (moved != null)
                            moved.SetAircraftActivity(plane, true);
                        result.Moved++;
                    }
                    else
                    {
                        result.Removed++;
                    }
                }
                if (existing.AIRCRAFT == null || existing.AIRCRAFT.Count == 0 || GameKey(existing.Game) != "DCS")
                    relation.NODES.Remove(existing);
            }
            foreach (KeyValuePair<string, bool> decision in add.Select(a => new KeyValuePair<string, bool>(a, true)).Concat(never.Select(n => new KeyValuePair<string, bool>(n, false))))
            {
                string game;
                string id;
                List<string> planes;
                if (!TryParseNode(decision.Key, out game, out id, out planes))
                {
                    result.Errors.Add("Not a node (game:id:plane1|plane2): " + decision.Key);
                    continue;
                }
                bool? axis = Program.TryInferNodeAxis(game, id);
                if (axis.HasValue && axis.Value != relation.ISAXIS)
                {
                    result.Errors.Add(game + ":" + id + " is " + (axis.Value ? "an axis" : "a button") + "; the relation is " + (relation.ISAXIS ? "an axis." : "a button."));
                    continue;
                }
                foreach (string plane in planes)
                {
                    RelationItem existing = (relation.NODES ?? new List<RelationItem>()).FirstOrDefault(n => n != null && string.Equals(n.ID, id, StringComparison.OrdinalIgnoreCase) &&
                                                                                                     string.Equals(GameKey(n.Game), GameKey(game), StringComparison.OrdinalIgnoreCase));
                    if (existing == null || existing.GetStateAircraft(plane) == PlaneState.NOT_EXISTENT)
                    {
                        if (!relation.AddNode(id, game, relation.ISAXIS, plane))
                        {
                            result.Errors.Add("Could not add " + game + ":" + id + " for " + plane + ".");
                            continue;
                        }
                        existing = relation.NODES.First(n => n != null && string.Equals(n.ID, id, StringComparison.OrdinalIgnoreCase) &&
                                                             string.Equals(GameKey(n.Game), GameKey(game), StringComparison.OrdinalIgnoreCase));
                    }
                    existing.SetAircraftActivity(plane, decision.Value);
                    if (decision.Value)
                        result.Added++;
                    else
                        result.Rejected++;
                }
            }
            return result;
        }

        // Entries that look wrong: the function catalog files the command under another function (in that aircraft), or
        // its title is the opposite direction of the relation (an old auto-fill swapped "+" and "-", "Up" and "Down").
        private static List<EngineRelationMisfit> Misfits(Relation relation, Context context, EngineAutoGroup.Slot slot)
        {
            Dictionary<string, EngineRelationMisfit> misfits = new Dictionary<string, EngineRelationMisfit>(StringComparer.OrdinalIgnoreCase);
            string relationText = Regex.Replace(relation.NAME ?? string.Empty, @"\s*\[[^\]]*\]$", string.Empty);
            foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
            {
                if (node == null || string.IsNullOrWhiteSpace(node.ID))
                    continue;
                string game = GameKey(node.Game);
                IEnumerable<string> planes = game == "DCS" ? (node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(p => p.Value).Select(p => p.Key) : new[] { game };
                foreach (string plane in planes)
                {
                    Command command = context.Find(game, node.ID, plane);
                    string title = command != null ? command.Title : node.ID;
                    string reason = string.Empty;
                    string belongs = string.Empty;
                    List<string> other = context.SlotsOfCommand(game, node.ID, plane);
                    // A catalog substitute for this function (the user's style: a free slot filled on purpose) is fine.
                    bool knownHere = slot != null && (other.Contains(slot.Id, StringComparer.OrdinalIgnoreCase) || context.IsSubstitute(slot.Id, game, node.ID, plane));
                    if (slot != null && other.Count > 0 && !knownHere)
                    {
                        EngineAutoGroup.Slot real;
                        belongs = context.Slots.TryGetValue(other[0], out real) ? real.Name : other[0];
                        reason = "The function catalog files '" + title + "' under '" + belongs + "'" + (game == "DCS" ? " in " + plane : string.Empty) + ", not '" + slot.Name + "'.";
                    }
                    else if (other.Count == 0 && !knownHere && command != null && InternalDataManagement.HaveOppositeDirections(relationText, command.Title))
                    {
                        reason = "'" + command.Title + "' is the opposite direction of '" + relationText + "'.";
                    }
                    if (reason.Length == 0)
                        continue;
                    string key = game + "|" + node.ID + "|" + belongs;
                    EngineRelationMisfit misfit;
                    if (!misfits.TryGetValue(key, out misfit))
                    {
                        misfit = new EngineRelationMisfit { Game = game, Id = node.ID, Title = title, Reason = reason, BelongsTo = belongs };
                        misfit.MoveTo = belongs.Length > 0 ? context.RelationForFunction(belongs, relation.ISAXIS, relation.NAME) : string.Empty;
                        misfits[key] = misfit;
                    }
                    if (!misfit.Planes.Contains(plane))
                        misfit.Planes.Add(plane);
                }
            }
            foreach (EngineRelationMisfit misfit in misfits.Values.Where(m => m.Planes.Count > 1 && m.Game == "DCS"))
                misfit.Reason = Regex.Replace(misfit.Reason, @" in [^,]+, not '", " in " + string.Join(", ", misfit.Planes.Take(4)) + (misfit.Planes.Count > 4 ? " ..." : string.Empty) + ", not '");
            return misfits.Values.ToList();
        }

        private static bool TryParseNode(string raw, out string game, out string id, out List<string> planes)
        {
            game = id = string.Empty;
            planes = new List<string>();
            string text = (raw ?? string.Empty).Trim();
            int first = text.IndexOf(':');
            int last = text.LastIndexOf(':');
            if (first <= 0 || last <= first)
                return false;
            game = text.Substring(0, first);
            id = text.Substring(first + 1, last - first - 1);
            planes = text.Substring(last + 1).Split('|').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            return id.Length > 0 && planes.Count > 0;
        }

        private static List<Candidate> Build(Relation relation, Context context, bool withTitles)
        {
            Dictionary<string, Candidate> candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // The relation's switched-on commands: they say what the relation means. Switched-off ones ("Never") only
            // keep their command from being proposed again.
            List<Command> mine = new List<Command>();
            foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
            {
                if (node == null || string.IsNullOrWhiteSpace(node.ID))
                    continue;
                string game = GameKey(node.Game);
                Dictionary<string, bool> aircraft = node.AIRCRAFT ?? new Dictionary<string, bool>();
                List<string> planes = aircraft.Keys.ToList();
                if (game != "DCS" && planes.Count == 0)
                    planes.Add(game);
                foreach (string plane in planes)
                {
                    present.Add(game + "|" + NodeId(game, node.ID) + "|" + plane);
                    bool active = !aircraft.ContainsKey(plane) || aircraft[plane];
                    Command command = active ? context.Find(game, node.ID, plane) : null;
                    if (command != null)
                        mine.Add(command);
                }
                // A command of another game counts as present for the whole game.
                if (game != "DCS")
                    present.Add(game + "|" + NodeId(game, node.ID) + "|" + game);
            }

            EngineAutoGroup.Slot slot = context.SlotOf(relation);
            context.LastSlot = slot;
            List<Meaning> meanings = context.MeaningsOf(relation, slot, mine);
            context.LastMeanings = meanings;
            Func<string, string, bool> allowed = context.PlaneFilter(relation, slot, mine);

            // 1. Function catalog: every function the relation's commands are (the union of their meanings), weighted by
            // how many of them agree; the function the relation is named after counts fully.
            foreach (Meaning meaning in meanings)
            {
                foreach (EngineAutoGroup.Row row in context.Rows.Where(r => string.Equals(r.Slot, meaning.Slot.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    string game = GameKey(row.Game);
                    string plane = game == "DCS" ? row.Plane : game;
                    if (present.Contains(game + "|" + NodeId(game, row.Action) + "|" + plane) || !allowed(game, plane))
                        continue;
                    Command command = context.Find(game, row.Action, plane);
                    if (command == null || command.IsAxis != relation.ISAXIS || Contradicted(context, command))
                        continue;
                    bool substitute = string.Equals(row.Status, "substitute", StringComparison.OrdinalIgnoreCase);
                    int score = substitute ? (int)Math.Round(45 + 25 * meaning.Weight) : (int)Math.Round(60 + 35 * meaning.Weight);
                    Candidate candidate = Get(candidates, command, score, "catalog");
                    candidate.Planes.Add(plane);
                    if (candidate.SlotName.Length == 0 || meaning.Weight >= 1)
                    {
                        candidate.SlotName = meaning.Slot.Name;
                        candidate.Reference = meaning.Reference;
                    }
                    (substitute ? candidate.SubstitutePlanes : candidate.CatalogPlanes).Add(Display(game, plane));
                }
            }

            // 2. The user's style: where an aircraft lacks the function, the function the user habitually puts there
            // (learned from at least two separate decisions in the user's own relations, like Auto Group does).
            Meaning main = meanings.FirstOrDefault(m => m.Weight >= 1);
            if (main != null && main.Slot.Substitutable)
            {
                List<KeyValuePair<string, int>> habits;
                if (context.Habits().TryGetValue(main.Slot.Id, out habits))
                {
                    HashSet<string> haveIt = new HashSet<string>(context.Rows.Where(r => string.Equals(r.Slot, main.Slot.Id, StringComparison.OrdinalIgnoreCase) &&
                                                                                       !string.Equals(r.Status, "substitute", StringComparison.OrdinalIgnoreCase))
                        .Select(r => GameKey(r.Game) == "DCS" ? r.Plane : GameKey(r.Game)), StringComparer.OrdinalIgnoreCase);
                    HashSet<string> filled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, int> habit in habits)
                    {
                        EngineAutoGroup.Slot from;
                        if (!context.Slots.TryGetValue(habit.Key, out from))
                            continue;
                        foreach (EngineAutoGroup.Row row in context.Rows.Where(r => string.Equals(r.Slot, habit.Key, StringComparison.OrdinalIgnoreCase) &&
                                                                                    !string.Equals(r.Status, "substitute", StringComparison.OrdinalIgnoreCase)))
                        {
                            string game = GameKey(row.Game);
                            string plane = game == "DCS" ? row.Plane : game;
                            if (haveIt.Contains(plane) || filled.Contains(plane) || present.Contains(game + "|" + NodeId(game, row.Action) + "|" + plane) || !allowed(game, plane))
                                continue;
                            Command command = context.Find(game, row.Action, plane);
                            if (command == null || command.IsAxis != relation.ISAXIS || Contradicted(context, command))
                                continue;
                            Candidate candidate = Get(candidates, command, 80, "your style");
                            candidate.Planes.Add(plane);
                            candidate.SlotName = main.Slot.Name;
                            candidate.HabitFrom = from.Name;
                            candidate.HabitCount = habit.Value;
                            candidate.HabitPlanes.Add(Display(game, plane));
                        }
                        // The strongest habit wins per aircraft.
                        foreach (EngineAutoGroup.Row row in context.Rows.Where(r => string.Equals(r.Slot, habit.Key, StringComparison.OrdinalIgnoreCase)))
                            filled.Add(GameKey(row.Game) == "DCS" ? row.Plane : GameKey(row.Game));
                    }
                }
            }

            // 3. Same DCS command id in more aircraft, only where the id means the same there: the aircraft's own title
            // and category of the id are the same (90) or near (75) to the relation's own aircraft (EngineIdMeanings).
            // The same id with another meaning ("Pitch" in a jet, "Collective" in a helicopter; a cockpit switch that is
            // another switch in every module) is never proposed for it.
            HashSet<string> activeIds = new HashSet<string>(mine.Where(m => m.Game == "DCS").Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            foreach (RelationItem node in (relation.NODES ?? new List<RelationItem>()).Where(n => n != null && GameKey(n.Game) == "DCS" && !string.IsNullOrWhiteSpace(n.ID) && activeIds.Contains(n.ID)))
            {
                List<Command> own = mine.Where(m => m.Game == "DCS" && string.Equals(m.Id, node.ID, StringComparison.OrdinalIgnoreCase)).ToList();
                List<EngineIdMeanings.Cluster> ownMeanings = EngineIdMeanings.Clusters(own);
                if (ownMeanings.Count == 0)
                    continue;
                Command reference = ownMeanings[0].Head.Sample;
                string covered = string.Join(", ", ownMeanings[0].Planes.Take(3));
                foreach (Command command in context.ById("DCS", node.ID))
                {
                    if (present.Contains("DCS|" + command.Id + "|" + command.Plane) || !allowed("DCS", command.Plane) || command.IsAxis != relation.ISAXIS)
                        continue;
                    string note;
                    EngineIdMeanings.Kind kind = EngineIdMeanings.Compare(reference, command, out note);
                    if (kind == EngineIdMeanings.Kind.Different)
                        continue;
                    bool sameTitle = kind == EngineIdMeanings.Kind.Same;
                    Candidate candidate = Get(candidates, command, sameTitle ? 90 : 75, "same id");
                    candidate.Planes.Add(command.Plane);
                    candidate.SameIdPlanes.Add(command.Plane);
                    candidate.SameIdNote = " as in " + (covered.Length > 0 ? covered : "this relation") + (sameTitle ? ", same title." : " ('" + reference.Title + "' there; " + note + ").");
                }
            }

            // 4. The function map: what the relation's actions are (catalog or the map's synonym rules) in every other
            // game and aircraft, also the ones the catalog does not know; same direction, same kind (axis / button).
            List<MapMeaning> mapMeanings = context.MapMeaningsOf(relation, mine);
            context.LastMapMeanings = mapMeanings;
            foreach (MapMeaning meaning in mapMeanings)
            {
                List<EngineFunctionMap.Entry> entries;
                if (!context.Map.ByFunction.TryGetValue(meaning.Function.Id, out entries))
                    continue;
                foreach (EngineFunctionMap.Entry entry in entries)
                {
                    Command command = entry.Command;
                    if (command.IsAxis != relation.ISAXIS || present.Contains(command.Game + "|" + command.Id + "|" + command.Plane) || !allowed(command.Game, command.Plane))
                        continue;
                    EngineFunctionMap.Assignment a = entry.Of(meaning.Function.Id);
                    if (EngineIdMeanings.CatalogAgainstTitle(entry, a))
                        continue;
                    if (a == null || a.Weight < 0.45 || !EngineFunctionMap.DirCompatible(a.Dir, meaning.Dir, command.IsAxis))
                        continue;
                    // Full agreement of a title that names the function is high; a game term, a side reading or a
                    // second function of the action is medium.
                    double strength = meaning.Weight * a.Weight;
                    int score = (int)Math.Round(50 + 30 * strength + (a.InTitle || a.Source == "catalog" ? 5 : 0));
                    Candidate candidate = Get(candidates, command, score, "function map");
                    candidate.Planes.Add(command.Plane);
                    candidate.MapPlanes.Add(Display(command.Game, command.Plane));
                    if (candidate.MapFunction.Length == 0 || score >= candidate.Score)
                    {
                        candidate.MapFunction = EngineFunctionMap.Describe(meaning.Function, a.Dir.Length > 0 ? a.Dir : meaning.Dir, command.IsAxis);
                        candidate.MapFunctionId = meaning.Function.Id;
                        candidate.MapReference = meaning.Reference;
                        candidate.MapTerm = a.Term;
                    }
                }
            }

            // 5. Title similarity (any game).
            if (withTitles)
            {
                List<string> references = mine.Select(m => m.Title).Concat(new[] { Regex.Replace(relation.NAME ?? string.Empty, @"\s*\[[^\]]*\]$", string.Empty) })
                    .Where(t => Tokens(t).Count > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (Command command in context.ByTokens(references.SelectMany(Tokens)))
                {
                    if (command.IsAxis != relation.ISAXIS || present.Contains(command.Game + "|" + command.Id + "|" + command.Plane) || !allowed(command.Game, command.Plane))
                        continue;
                    string best = string.Empty;
                    int score = 0;
                    foreach (string reference in references)
                    {
                        if (InternalDataManagement.HaveOppositeDirections(reference, command.Title))
                            continue;
                        int value = Similarity(reference, command.Title);
                        if (value > score)
                        {
                            score = value;
                            best = reference;
                        }
                    }
                    if (score < 70)
                        continue;
                    Candidate candidate = Get(candidates, command, score >= 90 ? 70 : score >= 80 ? 55 : 40, "title");
                    candidate.Planes.Add(command.Plane);
                    if (score > candidate.TitleScore)
                    {
                        candidate.TitleScore = score;
                        candidate.TitleReference = best;
                    }
                }
            }
            // Never an aircraft whose meaning of an id the relation already has differs from the relation's own meaning of
            // it (unless the function map reads its own title as the same function).
            List<Candidate> kept = new List<Candidate>();
            foreach (Candidate candidate in candidates.Values)
            {
                if (candidate.Game == "DCS" && candidate.Planes.Count > 0)
                {
                    List<Command> own = mine.Where(m => m.Game == "DCS" && string.Equals(m.Id, candidate.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                    Command sample = context.Find("DCS", candidate.Id, candidate.Planes.First());
                    if (own.Count > 0 && sample != null)
                    {
                        EngineIdMeanings.Cluster ownMeaning = EngineIdMeanings.MainCluster(EngineIdMeanings.Clusters(own), relation.NAME);
                        string note;
                        if (EngineIdMeanings.Compare(ownMeaning.Head.Sample, sample, out note) == EngineIdMeanings.Kind.Different &&
                            !EngineIdMeanings.SameFunction("DCS", candidate.Id, ownMeaning.Head.Planes[0], sample.Plane))
                            continue;
                    }
                }
                kept.Add(candidate);
            }
            return kept;
        }

        // The catalog files this command under a function only by its slot, while its own title names another function
        // and the id means different things in different aircraft (EngineIdMeanings.CatalogAgainstTitle).
        private static bool Contradicted(Context context, Command command)
        {
            EngineFunctionMap.Entry entry;
            if (command == null || command.Game != "DCS" || context.Map == null || !context.Map.ByKey.TryGetValue(command.Game + "|" + command.Id + "|" + command.Plane, out entry))
                return false;
            List<EngineFunctionMap.Assignment> catalog = entry.Functions.Where(a => a.Source == "catalog").ToList();
            return catalog.Count > 0 && catalog.All(a => EngineIdMeanings.CatalogAgainstTitle(entry, a));
        }

        // One candidate per id and meaning (EngineIdMeanings): a DCS id whose title means something else in some
        // aircraft is a candidate per meaning, never one row mixing them.
        private static Candidate Get(Dictionary<string, Candidate> candidates, Command command, int score, string source)
        {
            int cluster = command.Game == "DCS" ? EngineIdMeanings.ClusterIndex(command.Game, command.Id, command.Plane) : 0;
            string key = command.Game + "|" + command.Id + "|" + (cluster >= 0 ? cluster.ToString(CultureInfo.InvariantCulture) : "t:" + command.Title);
            Candidate candidate;
            if (!candidates.TryGetValue(key, out candidate))
            {
                candidate = new Candidate { Game = command.Game, Id = command.Id, Title = command.Title, Category = command.Category, IsAxis = command.IsAxis, Score = score, Source = source };
                candidates[key] = candidate;
            }
            else if (score > candidate.Score)
            {
                candidate.Score = score;
                candidate.Source = source;
            }
            return candidate;
        }

        private static string Confidence(int score)
        {
            return score >= 85 ? "high" : score >= 55 ? "medium" : "low";
        }

        private static int Rank(string confidence)
        {
            switch ((confidence ?? string.Empty).ToLowerInvariant())
            {
                case "high": return 3;
                case "medium": return 2;
                default: return 1;
            }
        }

        private static string Display(string game, string plane)
        {
            return game == "DCS" ? plane : game;
        }

        internal static string GameKey(string game)
        {
            return EngineAutoGroup.IsDcs(game) ? "DCS" : game.Trim();
        }

        // One spelling per command id for lookups: Star Citizen ids come as "actionmap|action" (the game lists) and as
        // "actionmap___action" (the function catalog, older profiles).
        internal static string NodeId(string game, string id)
        {
            if (id == null)
                return string.Empty;
            if (string.Equals(game, "StarCitizen", StringComparison.OrdinalIgnoreCase))
            {
                int split = id.IndexOf("___", StringComparison.Ordinal);
                if (split > 0 && id.IndexOf('|') < 0)
                    return id.Substring(0, split) + "|" + id.Substring(split + 3);
            }
            return id;
        }

        // Share of the shorter title's words found in the other one (0..100).
        private static int Similarity(string left, string right)
        {
            HashSet<string> a = Tokens(left);
            HashSet<string> b = Tokens(right);
            if (a.Count == 0 || b.Count == 0)
                return 0;
            int common = a.Count(b.Contains);
            if (common < 2 && !(a.Count == 1 && b.Count == 1 && common == 1))
                return 0;
            int smaller = Math.Min(a.Count, b.Count);
            int larger = Math.Max(a.Count, b.Count);
            // Penalise a one-word overlap between long titles.
            return (int)Math.Round(100.0 * common / smaller * (0.6 + 0.4 * smaller / larger));
        }

        internal static HashSet<string> Tokens(string text)
        {
            string spaced = Regex.Replace(text ?? string.Empty, "([a-z])([A-Z])", "$1 $2").Replace('_', ' ');
            return new HashSet<string>(Regex.Split(spaced.ToLowerInvariant(), "[^a-z0-9+-]+").Where(t => t.Length > 1 && !StopWords.Contains(t)), StringComparer.Ordinal);
        }

        private static string Normalize(string text)
        {
            return string.Join(" ", Tokens(text).OrderBy(t => t, StringComparer.Ordinal));
        }

        // Every command of every game, the function catalog and who uses what (built once per engine run).
        private sealed class Context
        {
            public List<EngineAutoGroup.Row> Rows = new List<EngineAutoGroup.Row>();
            public Dictionary<string, EngineAutoGroup.Slot> Slots = new Dictionary<string, EngineAutoGroup.Slot>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, EngineAutoGroup.Aircraft> Aircraft = new Dictionary<string, EngineAutoGroup.Aircraft>(StringComparer.OrdinalIgnoreCase);
            public EngineAutoGroup.Slot LastSlot;
            public List<Meaning> LastMeanings = new List<Meaning>();
            public EngineFunctionMap.Map Map;
            public List<MapMeaning> LastMapMeanings = new List<MapMeaning>();
            public List<string> LearnedRules = new List<string>();
            private Dictionary<string, List<KeyValuePair<string, int>>> habits;
            private Dictionary<string, Command> byKey = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase);
            private Dictionary<string, List<Command>> byId = new Dictionary<string, List<Command>>(StringComparer.OrdinalIgnoreCase);
            private Dictionary<string, List<Command>> byToken = new Dictionary<string, List<Command>>(StringComparer.Ordinal);
            private Dictionary<string, List<string>> users = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            private Dictionary<string, string> slotByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private Dictionary<string, List<string>> slotsByCommand = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            public static Context Load(List<string> warnings)
            {
                Context context = new Context();
                context.Map = EngineFunctionMap.Load(warnings);
                foreach (Command command in Index(warnings))
                {
                    string key = command.Game + "|" + command.Id + "|" + command.Plane;
                    if (context.byKey.ContainsKey(key))
                        continue;
                    context.byKey[key] = command;
                    Add(context.byId, command.Game + "|" + command.Id, command);
                    foreach (string token in Tokens(command.Title))
                        Add(context.byToken, token, command);
                }

                EngineAutoGroupResult errors = new EngineAutoGroupResult();
                Dictionary<string, EngineAutoGroup.Slot> slots;
                Dictionary<string, EngineAutoGroup.Aircraft> aircraft;
                List<EngineAutoGroup.Row> rows;
                if (EngineAutoGroup.TryLoadCatalog(EngineNlGroup.FunctionsFolder(), errors, out slots, out aircraft, out rows))
                {
                    context.Slots = slots;
                    context.Aircraft = aircraft;
                    context.Rows = rows.Where(r => !string.Equals(r.Status, "rejected", StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (EngineAutoGroup.Slot slot in slots.Values.Where(s => !context.slotByName.ContainsKey(s.Name)))
                        context.slotByName[slot.Name] = slot.Id;
                    foreach (EngineAutoGroup.Row row in context.Rows.Where(r => !string.Equals(r.Status, "substitute", StringComparison.OrdinalIgnoreCase)))
                    {
                        string game = GameKey(row.Game);
                        List<string> list;
                        string key = game + "|" + NodeId(game, row.Action) + "|" + (game == "DCS" ? row.Plane : game);
                        if (!context.slotsByCommand.TryGetValue(key, out list))
                            context.slotsByCommand[key] = list = new List<string>();
                        list.Add(row.Slot);
                    }
                }
                else
                {
                    warnings.AddRange(errors.Errors);
                }

                foreach (Relation relation in InternalDataManagement.AllRelations.Values.Where(r => r != null))
                {
                    foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
                    {
                        if (node == null)
                            continue;
                        string game = GameKey(node.Game);
                        IEnumerable<string> planes = game == "DCS" ? (node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(p => p.Value).Select(p => p.Key) : new[] { game };
                        foreach (string plane in planes)
                            Add(context.users, game + "|" + NodeId(game, node.ID) + "|" + plane, relation.NAME);
                    }
                }
                return context;
            }

            private static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
            {
                List<T> list;
                if (!map.TryGetValue(key, out list))
                    map[key] = list = new List<T>();
                if (!list.Contains(value))
                    list.Add(value);
            }

            public Command Find(string game, string id, string plane)
            {
                Command command;
                return byKey.TryGetValue(game + "|" + NodeId(game, id) + "|" + plane, out command) ? command : null;
            }

            public IEnumerable<Command> ById(string game, string id)
            {
                List<Command> list;
                return byId.TryGetValue(game + "|" + id, out list) ? list : Enumerable.Empty<Command>();
            }

            public IEnumerable<Command> ByTokens(IEnumerable<string> tokens)
            {
                HashSet<Command> seen = new HashSet<Command>();
                foreach (string token in tokens.Distinct())
                {
                    List<Command> list;
                    // Very common words ("mode", "left") alone are no lead; their commands come in through rarer words.
                    if (!byToken.TryGetValue(token, out list) || list.Count > 4000)
                        continue;
                    foreach (Command command in list)
                    {
                        if (seen.Add(command))
                            yield return command;
                    }
                }
            }

            private HashSet<string> substitutes;

            public bool IsSubstitute(string slot, string game, string id, string plane)
            {
                if (substitutes == null)
                {
                    substitutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (EngineAutoGroup.Row row in Rows.Where(r => string.Equals(r.Status, "substitute", StringComparison.OrdinalIgnoreCase)))
                    {
                        string rowGame = GameKey(row.Game);
                        substitutes.Add(row.Slot + "|" + rowGame + "|" + NodeId(rowGame, row.Action) + "|" + (rowGame == "DCS" ? row.Plane : rowGame));
                    }
                }
                return substitutes.Contains(slot + "|" + game + "|" + NodeId(game, id) + "|" + plane);
            }

            public List<string> SlotsOfCommand(string game, string id, string plane)
            {
                List<string> list;
                return slotsByCommand.TryGetValue(game + "|" + NodeId(game, id) + "|" + plane, out list) ? list.Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
            }

            private Dictionary<string, List<string>> relationsByFunction;

            // The relation that stands for a function (named after it, or most of its commands are it), to move an entry there.
            public string RelationForFunction(string functionName, bool axis, string self)
            {
                if (relationsByFunction == null)
                {
                    relationsByFunction = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (Relation relation in InternalDataManagement.AllRelations.Values.Where(r => r != null && r.NODES != null && r.NODES.Count > 0))
                    {
                        EngineAutoGroup.Slot slot = SlotOf(relation);
                        if (slot != null)
                            Add(relationsByFunction, slot.Name, relation.NAME);
                    }
                }
                List<string> names;
                if (!relationsByFunction.TryGetValue(functionName, out names))
                    return string.Empty;
                return names.Where(n => n != self && InternalDataManagement.AllRelations[n].ISAXIS == axis)
                    .OrderBy(n => string.Equals(n, functionName, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? string.Empty;
            }

            public List<string> OtherRelations(string game, string id, IEnumerable<string> planes, string self)
            {
                List<string> others = new List<string>();
                foreach (string plane in planes)
                {
                    List<string> list;
                    if (users.TryGetValue(game + "|" + id + "|" + plane, out list))
                        others.AddRange(list.Where(r => !string.Equals(r, self, StringComparison.OrdinalIgnoreCase)));
                }
                return others.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
            }

            // The relation's function: its name (Auto Group names relations after the function, with an optional split
            // suffix), else the function most of its commands belong to.
            public EngineAutoGroup.Slot SlotOf(Relation relation)
            {
                string name = Regex.Replace(relation.NAME ?? string.Empty, @"\s*\[[^\]]*\]$", string.Empty);
                string slotId;
                if (slotByName.TryGetValue(name, out slotId))
                    return Slots[slotId];
                Dictionary<string, int> votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int nodes = 0;
                foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
                {
                    if (node == null)
                        continue;
                    string game = GameKey(node.Game);
                    IEnumerable<string> planes = game == "DCS" ? (node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(p => p.Value).Select(p => p.Key) : new[] { game };
                    foreach (string plane in planes)
                    {
                        nodes++;
                        List<string> list;
                        if (!slotsByCommand.TryGetValue(game + "|" + NodeId(game, node.ID) + "|" + plane, out list))
                            continue;
                        foreach (string slot in list.Distinct())
                            votes[slot] = (votes.ContainsKey(slot) ? votes[slot] : 0) + 1;
                    }
                }
                if (votes.Count == 0)
                    return null;
                KeyValuePair<string, int> top = votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal).First();
                // Most of the relation's commands must agree, else the relation is no single function.
                return top.Value * 2 >= nodes && Slots.ContainsKey(top.Key) ? Slots[top.Key] : null;
            }

            // The user's substitution habits (Auto Group's learned style), built once.
            public Dictionary<string, List<KeyValuePair<string, int>>> Habits()
            {
                if (habits == null)
                    habits = Slots.Count == 0
                        ? new Dictionary<string, List<KeyValuePair<string, int>>>(StringComparer.OrdinalIgnoreCase)
                        : EngineAutoGroup.LearnHabits(Slots, Aircraft, Rows, LearnedRules);
                return habits;
            }

            // Every function the relation's switched-on commands are, weighted: share of the commands that are it,
            // doubled and capped at 1 (half of them agreeing = full weight). The function of the relation's name (or the
            // single function SlotOf found) counts fully. Functions only a few commands share (below 20 %) are noise.
            public List<Meaning> MeaningsOf(Relation relation, EngineAutoGroup.Slot named, List<Command> mine)
            {
                Dictionary<string, Meaning> meanings = new Dictionary<string, Meaning>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, int> votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Command command in mine)
                {
                    foreach (string slot in SlotsOfCommand(command.Game, command.Id, command.Plane))
                    {
                        votes[slot] = (votes.ContainsKey(slot) ? votes[slot] : 0) + 1;
                        if (!references.ContainsKey(slot))
                            references[slot] = Display(command.Game, command.Plane) + " '" + command.Title + "'";
                    }
                }
                int total = Math.Max(1, mine.Count);
                foreach (KeyValuePair<string, int> vote in votes)
                {
                    EngineAutoGroup.Slot slot;
                    double share = (double)vote.Value / total;
                    if (!Slots.TryGetValue(vote.Key, out slot) || slot.Axis != relation.ISAXIS || share < 0.2)
                        continue;
                    meanings[slot.Id] = new Meaning { Slot = slot, Weight = Math.Min(1.0, share * 2), Reference = references[vote.Key] };
                }
                if (named != null)
                {
                    Meaning meaning;
                    if (!meanings.TryGetValue(named.Id, out meaning))
                        meanings[named.Id] = meaning = new Meaning { Slot = named, Reference = references.ContainsKey(named.Id) ? references[named.Id] : string.Empty };
                    meaning.Weight = 1.0;
                }
                return meanings.Values.OrderByDescending(m => m.Weight).ThenBy(m => m.Slot.Id, StringComparer.Ordinal).ToList();
            }

            // What the relation is in the function map: every function its switched-on actions are (catalog or the map's
            // rules), weighted like MeaningsOf (half of the actions agreeing = full weight, below 20 % ignored), with the
            // direction; the relation's name counts fully when it is a function's own words ("Throttle", "Gear up").
            public List<MapMeaning> MapMeaningsOf(Relation relation, List<Command> mine)
            {
                Dictionary<string, MapMeaning> meanings = new Dictionary<string, MapMeaning>(StringComparer.OrdinalIgnoreCase);
                if (Map == null || Map.Functions.Count == 0)
                    return new List<MapMeaning>();
                Dictionary<string, double> votes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, KeyValuePair<EngineFunctionMap.Function, string>> keys = new Dictionary<string, KeyValuePair<EngineFunctionMap.Function, string>>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Command command in mine)
                {
                    EngineFunctionMap.Entry entry;
                    if (!Map.ByKey.TryGetValue(command.Game + "|" + command.Id + "|" + command.Plane, out entry))
                        continue;
                    foreach (EngineFunctionMap.Assignment a in entry.Functions.Where(f => f.Weight >= 0.5))
                    {
                        string key = a.Function.Id + "|" + a.Dir;
                        votes[key] = (votes.ContainsKey(key) ? votes[key] : 0) + a.Weight;
                        keys[key] = new KeyValuePair<EngineFunctionMap.Function, string>(a.Function, a.Dir);
                        if (!references.ContainsKey(key))
                            references[key] = Display(command.Game, command.Plane) + " '" + command.Title + "'";
                    }
                }
                int total = Math.Max(1, mine.Count);
                foreach (KeyValuePair<string, double> vote in votes)
                {
                    double share = vote.Value / total;
                    EngineFunctionMap.Function f = keys[vote.Key].Key;
                    if (share < 0.2 || (f.Only == "axis" && !relation.ISAXIS) || (f.Only == "button" && relation.ISAXIS))
                        continue;
                    meanings[vote.Key] = new MapMeaning { Function = f, Dir = keys[vote.Key].Value, Weight = Math.Min(1.0, share * 2), Reference = references[vote.Key] };
                }
                string name = Regex.Replace(relation.NAME ?? string.Empty, @"\s*\[[^\]]*\]$", string.Empty);
                string[] nameWords = EngineFunctionMap.Tokenize(name).Where(t => !Map.Stopwords.Contains(t)).ToArray();
                if (nameWords.Length > 0)
                {
                    EngineFunctionMap.Understanding u = EngineFunctionMap.Resolve(Map, name);
                    int covered = u.Functions.Where(r => !r.Related).Sum(r => r.Covered) + (u.Dir.Length > 0 ? 1 : 0);
                    // The name must be the function's words, not a sentence that happens to contain one.
                    if (u.Extra.Count == 0 && covered >= nameWords.Length * 0.6)
                    {
                        // A function the name only "describes" (a stray number: "A10" ~ "F10 map") does not count when
                        // the name names a function by its own words ("DMS").
                        bool named = u.Functions.Any(x => !x.Related && !x.Described);
                        foreach (EngineFunctionMap.Resolved r in u.Functions.Where(x => !x.Related && !(named && x.Described)))
                        {
                            if ((r.Function.Only == "axis" && !relation.ISAXIS) || (r.Function.Only == "button" && relation.ISAXIS))
                                continue;
                            string dir = relation.ISAXIS && !r.Function.AxisDirs ? string.Empty : r.Dir;
                            string key = r.Function.Id + "|" + dir;
                            MapMeaning meaning;
                            if (!meanings.TryGetValue(key, out meaning))
                                meanings[key] = meaning = new MapMeaning { Function = r.Function, Dir = dir, Reference = references.ContainsKey(key) ? references[key] : string.Empty };
                            meaning.Weight = 1.0;
                        }
                    }
                }
                return meanings.Values.OrderByDescending(m => m.Weight).ThenBy(m => m.Function.Id, StringComparer.Ordinal).ToList();
            }

            // Planes the relation may grow into: an Auto Group split suffix ("[Fixed wing]", "[Rotary wing, WW2]", per
            // aircraft) keeps the split; without one every plane is fine.
            public Func<string, string, bool> PlaneFilter(Relation relation, EngineAutoGroup.Slot slot, List<Command> mine)
            {
                Match match = Regex.Match(relation.NAME ?? string.Empty, @"(\s*\[[^\]]*\])$");
                if (!match.Success)
                    return (game, plane) => true;
                string suffix = " " + match.Groups[1].Value.Trim();
                EngineAutoGroup.Slot basis = slot ?? new EngineAutoGroup.Slot();
                List<string> existing = mine.Select(m => m.Plane).Distinct(StringComparer.OrdinalIgnoreCase).Where(p => Aircraft.ContainsKey(p)).ToList();
                List<string> presets = new[] { "wing", "era", "purist" }
                    .Where(p => existing.Count > 0 && existing.All(e => EngineAutoGroup.Suffix(p, basis, e, Aircraft[e]) == suffix)).ToList();
                if (presets.Count == 0)
                    return (game, plane) => existing.Contains(plane, StringComparer.OrdinalIgnoreCase);
                return (game, plane) =>
                {
                    EngineAutoGroup.Aircraft aircraft;
                    return Aircraft.TryGetValue(plane, out aircraft) && presets.Any(p => EngineAutoGroup.Suffix(p, basis, plane, aircraft) == suffix);
                };
            }
        }

        // Every action of every game list on this PC, read once per run. The function map keeps a mapped copy on disk
        // (EngineFunctionMapCache), so a run whose lists did not change reads that instead of every list.
        internal static List<Command> Index(List<string> warnings)
        {
            if (index != null)
                return index;
            EngineFunctionMap.Load(warnings);
            if (index == null)
                index = Parse(warnings);
            return index;
        }

        internal static void SetIndex(List<Command> commands)
        {
            index = commands;
        }

        internal static void ResetIndex()
        {
            index = null;
        }

        // A folder laid out like DB\ (DCS\*.html, IL2\IL2.actions, StarCitizen\AllBinds.xml, <Game>\actions.tsv) that
        // replaces the game lists, for tests on fixtures only.
        internal static string IndexOverrideFolder()
        {
            string folder = Environment.GetEnvironmentVariable("JOYPRO_ACTION_INDEX_DIR");
            return string.IsNullOrWhiteSpace(folder) ? string.Empty : folder.Trim();
        }

        private static string ListFolder(string game)
        {
            string over = IndexOverrideFolder();
            return over.Length > 0 ? Path.Combine(over, game) : GameDataPaths.DbFolder(game);
        }

        // The files Parse reads (for the cache's fingerprint).
        internal static List<string> SourceFiles()
        {
            List<string> files = new List<string>();
            string dcs = ListFolder("DCS");
            if (Directory.Exists(dcs))
                files.AddRange(Directory.GetFiles(dcs, "*.html").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            files.Add(Path.Combine(ListFolder("IL2"), "IL2.actions"));
            files.Add(Path.Combine(ListFolder("StarCitizen"), "AllBinds.xml"));
            foreach (IGameIntegration game in GameRegistry.All)
                files.Add(IndexOverrideFolder().Length > 0 ? Path.Combine(ListFolder(game.DbFolder), GameCatalogStore.FileName) : GameCatalogStore.CatalogPath(game));
            return files;
        }

        // DCS: DB/DCS/<plane>.html; IL-2: IL2.actions; Star Citizen: AllBinds.xml; registry games: their catalogs.
        internal static List<Command> Parse(List<string> warnings)
        {
            List<Command> commands = new List<Command>();
            string dcs = ListFolder("DCS");
            if (Directory.Exists(dcs))
            {
                foreach (string file in Directory.GetFiles(dcs, "*.html").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string plane = Path.GetFileNameWithoutExtension(file);
                    try
                    {
                        foreach (DCSInput input in InternalDataManagement.ParseDcsHtmlInputs(file, plane))
                            commands.Add(new Command { Game = "DCS", Plane = plane, Id = input.ID, Title = input.Title ?? string.Empty, Category = input.Cat ?? string.Empty, IsAxis = input.IsAxis });
                    }
                    catch (Exception ex)
                    {
                        warnings?.Add("DCS database " + Path.GetFileName(file) + ": " + ex.Message);
                    }
                }
            }

            string il2 = Path.Combine(ListFolder("IL2"), "IL2.actions");
            if (File.Exists(il2))
            {
                foreach (string line in File.ReadAllLines(il2))
                {
                    Match match = Regex.Match(line, @"^\s*([A-Za-z0-9_]+)\s*,.*//\s*([ab])\s*(.*)$");
                    if (match.Success)
                        commands.Add(new Command { Game = "IL2Game", Plane = "IL2Game", Id = match.Groups[1].Value, Title = match.Groups[3].Value.Trim(), IsAxis = match.Groups[2].Value == "a" });
                }
            }

            string sc = Path.Combine(ListFolder("StarCitizen"), "AllBinds.xml");
            if (File.Exists(sc))
            {
                string category = string.Empty;
                foreach (string line in File.ReadAllLines(sc))
                {
                    Match map = Regex.Match(line, "<actionmap name=\"([^\"]+)\"");
                    if (map.Success)
                        category = map.Groups[1].Value;
                    Match action = Regex.Match(line, "<action name=\"([^\"]+)\"><!--[^/]*//(true|false)");
                    if (action.Success)
                    {
                        string id = action.Groups[1].Value;
                        commands.Add(new Command { Game = "StarCitizen", Plane = "StarCitizen", Id = category.Length > 0 ? category + "|" + id : id, Title = id.Replace('_', ' '), Category = category, IsAxis = action.Groups[2].Value == "true" });
                    }
                }
            }

            string over = IndexOverrideFolder();
            foreach (IGameIntegration game in GameRegistry.All)
            {
                if (over.Length == 0)
                {
                    foreach (GameAction action in GameCatalogStore.Load(game, warnings).Actions)
                        commands.Add(new Command { Game = game.GameId, Plane = game.GameId, Id = action.Id, Title = action.Title, Category = action.Category, IsAxis = action.IsAxis });
                    continue;
                }
                string file = Path.Combine(ListFolder(game.DbFolder), GameCatalogStore.FileName);
                if (!File.Exists(file))
                    continue;
                foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith("#", StringComparison.Ordinal))
                        continue;
                    string[] parts = raw.Split('\t');
                    if (parts.Length >= 3 && parts[0].Trim().Length > 0)
                        commands.Add(new Command { Game = game.GameId, Plane = game.GameId, Id = parts[0].Trim(), Title = parts[1].Trim(), IsAxis = string.Equals(parts[2].Trim(), "axis", StringComparison.OrdinalIgnoreCase), Category = parts.Length > 3 ? parts[3].Trim() : string.Empty });
                }
            }
            return commands;
        }

        public static string ToText(EngineRelationSuggestResult result)
        {
            StringBuilder sb = new StringBuilder();
            if (result.Relations.Count > 0 || result.Relation.Length == 0)
            {
                sb.AppendLine("Relations With Suggestions: " + result.Relations.Count.ToString(CultureInfo.InvariantCulture));
                foreach (EngineRelationSuggestSummary summary in result.Relations)
                {
                    sb.AppendLine("  - " + summary.Relation + " | " + summary.Suggestions.ToString(CultureInfo.InvariantCulture) + " suggestions, " + summary.High.ToString(CultureInfo.InvariantCulture) + " high" +
                                  (summary.Misfits > 0 ? ", " + summary.Misfits.ToString(CultureInfo.InvariantCulture) + " look wrong" : string.Empty) +
                                  (summary.Items != null ? ", " + summary.NewGames.ToString(CultureInfo.InvariantCulture) + " new games, " + summary.UsedElsewhere.ToString(CultureInfo.InvariantCulture) + " used elsewhere" : string.Empty));
                    foreach (EngineRelationSuggestion s in summary.Items ?? new List<EngineRelationSuggestion>())
                        sb.AppendLine("      + " + s.Game + ":" + s.Id + ":" + string.Join("|", s.Planes) + " | " + s.Title + " | " + s.Missing +
                                      (s.NewPlanes.Count > 0 && s.Game == "DCS" ? " (" + string.Join(", ", s.NewPlanes) + ")" : string.Empty) + " | " + s.Confidence + " " + s.Score.ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {
                sb.AppendLine("Relation: " + result.Relation);
                sb.AppendLine("Function: " + (result.SlotName.Length > 0 ? result.SlotName + " (" + result.Slot + ")" : "(not in the function catalog)"));
                if (result.Functions.Count > 0)
                    sb.AppendLine("Meanings: " + string.Join("; ", result.Functions.Select(f => f.Name + " " + ((int)Math.Round(f.Weight * 100)).ToString(CultureInfo.InvariantCulture) + "%" +
                                                                                                     (f.Reference.Length > 0 ? " (" + f.Reference + ")" : string.Empty))));
                if (result.MapFunctions.Count > 0)
                    sb.AppendLine("Function map: " + string.Join("; ", result.MapFunctions.Select(f => f.Name + (f.Dir.Length > 0 ? " " + EngineFunctionMap.DirWord(f.Dir) : string.Empty) + " " +
                                                                                                         ((int)Math.Round(f.Weight * 100)).ToString(CultureInfo.InvariantCulture) + "%")));
                if (result.Dismissed.Count > 0)
                {
                    sb.AppendLine("Dismissed: " + result.Dismissed.Count.ToString(CultureInfo.InvariantCulture));
                    foreach (EngineRelationDismissed d in result.Dismissed)
                        sb.AppendLine("  x " + d.Game + ":" + d.Id + ":" + string.Join("|", d.Planes) + " | " + d.Title);
                }
                sb.AppendLine("Looks Wrong: " + result.Misfits.Count.ToString(CultureInfo.InvariantCulture));
                foreach (EngineRelationMisfit m in result.Misfits)
                    sb.AppendLine("  ! " + m.Game + ":" + m.Id + ":" + string.Join("|", m.Planes) + " | " + m.Reason + (m.MoveTo.Length > 0 ? " Move to '" + m.MoveTo + "'." : string.Empty));
                sb.AppendLine("Suggestions: " + result.Suggestions.Count.ToString(CultureInfo.InvariantCulture));
                foreach (EngineRelationSuggestion s in result.Suggestions)
                {
                    sb.AppendLine("  - [" + s.Confidence + "] " + s.Game + ":" + s.Id + ":" + string.Join("|", s.Planes) + " | " + s.Title +
                                  (s.InOtherRelations.Count > 0 ? " | in " + string.Join(", ", s.InOtherRelations) : string.Empty) +
                                  (s.MeaningNote.Length > 0 ? " | " + s.MeaningNote : string.Empty));
                    foreach (string reason in s.Reasons.Take(3))
                        sb.AppendLine("      " + reason);
                }
            }
            foreach (string warning in result.Warnings.Distinct().Take(10))
                sb.AppendLine("Warning: " + warning);
            foreach (string error in result.Errors)
                sb.AppendLine("Error: " + error);
            return sb.ToString();
        }
    }

    public sealed class EngineRelationSuggestion
    {
        public string Game { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public string Category { get; set; }
        public bool IsAxis { get; set; }
        public List<string> Planes { get; set; }
        public string Confidence { get; set; }
        public int Score { get; set; }
        public string Source { get; set; }
        // One line for a list row ("Same function as F-16C_50 'Countermeasures Management Switch - Fwd'").
        public string Why { get; set; }
        public List<string> Reasons { get; set; }
        public List<string> InOtherRelations { get; set; }
        // The function map's function id when the map proposed it (or agrees), else empty.
        public string Function { get; set; }
        // DCS: every meaning of the id (EngineIdMeanings); see EngineRelationSearchHit.
        public List<EngineAircraftVariant> Variants { get; set; }
        public int Near { get; set; }
        public int SameIdDifferent { get; set; }
        public string MeaningNote { get; set; }
        // What the relation lacks: "game" (no action of this game yet), "aircraft" (DCS aircraft it has no action for:
        // NewPlanes) or empty (one more action for aircraft it covers already).
        public string Missing { get; set; }
        public bool NewGame { get; set; }
        public List<string> NewPlanes { get; set; }

        public EngineRelationSuggestion()
        {
            Game = Id = Title = Category = Confidence = Source = Why = Function = MeaningNote = Missing = string.Empty;
            Planes = new List<string>();
            NewPlanes = new List<string>();
            Reasons = new List<string>();
            InOtherRelations = new List<string>();
            Variants = new List<EngineAircraftVariant>();
        }
    }

    // A function of the function map the relation is (from its actions or its name), with its direction and weight.
    public sealed class EngineRelationMapFunction
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Dir { get; set; }
        public double Weight { get; set; }
        public string Reference { get; set; }

        public EngineRelationMapFunction()
        {
            Id = Name = Dir = Reference = string.Empty;
        }
    }

    // A function the relation stands for, with how much it counts for the suggestions (1 = fully).
    public sealed class EngineRelationFunction
    {
        public string Slot { get; set; }
        public string Name { get; set; }
        public double Weight { get; set; }
        public string Reference { get; set; }

        public EngineRelationFunction()
        {
            Slot = Name = Reference = string.Empty;
        }
    }

    // A command the user dismissed in this relation (in it switched off); "restore" takes it out so it can come back.
    public sealed class EngineRelationDismissed
    {
        public string Game { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public List<string> Planes { get; set; }

        public EngineRelationDismissed()
        {
            Game = Id = Title = string.Empty;
            Planes = new List<string>();
        }
    }

    public sealed class EngineRelationSuggestSummary
    {
        public string Relation { get; set; }
        public int Suggestions { get; set; }
        public int High { get; set; }
        public int Planes { get; set; }
        public int Misfits { get; set; }
        // --missing: the relation's kind, how many new games, how many fitting actions another relation uses, and the
        // free suggestions themselves (new games first).
        public bool IsAxis { get; set; }
        public int NewGames { get; set; }
        public int UsedElsewhere { get; set; }
        public List<EngineRelationSuggestion> Items { get; set; }
    }

    // An entry of a relation that looks wrong, with where it belongs.
    public sealed class EngineRelationMisfit
    {
        public string Game { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public List<string> Planes { get; set; }
        public string Reason { get; set; }
        public string BelongsTo { get; set; }
        public string MoveTo { get; set; }

        public EngineRelationMisfit()
        {
            Game = Id = Title = Reason = BelongsTo = MoveTo = string.Empty;
            Planes = new List<string>();
        }
    }

    public sealed class EngineRelationSuggestResult
    {
        public string Relation { get; set; }
        public string Slot { get; set; }
        public string SlotName { get; set; }
        public bool IsAxis { get; set; }
        public List<EngineRelationFunction> Functions { get; set; }
        // What the relation is in the function map (EngineFunctionMap): function id, direction, weight.
        public List<EngineRelationMapFunction> MapFunctions { get; set; }
        public List<EngineRelationSuggestion> Suggestions { get; set; }
        public List<EngineRelationMisfit> Misfits { get; set; }
        public List<EngineRelationDismissed> Dismissed { get; set; }
        public List<EngineRelationSuggestSummary> Relations { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public EngineRelationSuggestResult()
        {
            Relation = Slot = SlotName = string.Empty;
            Functions = new List<EngineRelationFunction>();
            MapFunctions = new List<EngineRelationMapFunction>();
            Suggestions = new List<EngineRelationSuggestion>();
            Misfits = new List<EngineRelationMisfit>();
            Dismissed = new List<EngineRelationDismissed>();
            Relations = new List<EngineRelationSuggestSummary>();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }

    public sealed class EngineRelationSuggestApplyResult
    {
        public string Relation { get; set; }
        public int Added { get; set; }
        public int Rejected { get; set; }
        public int Removed { get; set; }
        public int Moved { get; set; }
        // --take: aircraft taken out of other relations (named in TakenFrom) and added here.
        public int Taken { get; set; }
        public List<string> TakenFrom { get; set; }
        public List<string> Errors { get; set; }

        public EngineRelationSuggestApplyResult()
        {
            Relation = string.Empty;
            TakenFrom = new List<string>();
            Errors = new List<string>();
        }
    }
}
