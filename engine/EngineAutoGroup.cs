using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    // "Auto Group": builds relations from the function catalog in DB\Functions: slots.json, aircraft.json and one
    // <Game>.tsv per game (DCS.tsv, StarCitizen.tsv, IL2Game.tsv, MSFS.tsv ...). The catalog maps every aircraft's (or
    // game's) commands to shared function slots, verified against the manuals, so one relation spans all games.
    // Presets decide how far a slot is shared: all aircraft, split by fixed/rotary wing, by wing type and era, or
    // "purist" (only the basic flight controls shared, everything else per aircraft).
    internal static class EngineAutoGroup
    {
        public const string GroupTag = "AutoGroup";
        private const int MinHabit = 2;
        public static readonly string[] Presets = { "all", "wing", "era", "purist" };

        internal sealed class Slot
        {
            public string Id = string.Empty;
            public string Name = string.Empty;
            public string Kind = string.Empty;
            public bool Axis;
            public bool Basic;
            public string Scope = string.Empty;

            // Display buttons, keypad keys and aircraft-specific slots only take their own aircraft's command.
            public bool Substitutable
            {
                get { return Kind != "mfd-osb" && Kind != "ufc-key" && Scope != "aircraft"; }
            }
        }

        internal sealed class Aircraft
        {
            public string Game = "DCS";
            public string Type = string.Empty;
            public string Era = string.Empty;
            public string VariantOf = string.Empty;
        }

        internal sealed class Row
        {
            public string Game = "DCS";
            public string Plane;
            public string Slot;
            public string Action;
            public string Status;
        }

        private sealed class Group
        {
            public Slot Slot;
            public string Name;
            public List<Row> Rows = new List<Row>();
        }

        public static EngineAutoGroupResult Run(string preset, bool includeSubstitutes, ICollection<string> planeFilter, bool replace, bool dryRun)
        {
            return Run(preset, includeSubstitutes, planeFilter, replace, dryRun, true);
        }

        // useMap: also the function map (EngineFunctionMap) for the actions, aircraft and games the catalog does not
        // know yet: each function goes to the slot the catalog (the user's style) uses most for it.
        public static EngineAutoGroupResult Run(string preset, bool includeSubstitutes, ICollection<string> planeFilter, bool replace, bool dryRun, bool useMap)
        {
            EngineAutoGroupResult result = new EngineAutoGroupResult { Preset = preset, IncludeSubstitutes = includeSubstitutes, Replace = replace, DryRun = dryRun, UseMap = useMap };
            string folder = EngineNlGroup.FunctionsFolder();
            Dictionary<string, Slot> slots;
            Dictionary<string, Aircraft> aircraft;
            List<Row> rows;
            if (!TryLoadCatalog(folder, result, out slots, out aircraft, out rows, true))
                return result;
            // Only the tables of the files: with the map Auto Group maps what they do not know itself, and --no-map
            // means the hand-made tables alone (no derived rows either).
            HashSet<string> rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(folder, "*.rejected.tsv"))
                rejected.UnionWith(LoadRejected(file));

            HashSet<string> filter = new HashSet<string>(planeFilter ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> dbIds = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            List<Row> usable = new List<Row>();
            foreach (Row row in rows)
            {
                Slot slot;
                if (!slots.TryGetValue(row.Slot, out slot) || !aircraft.ContainsKey(row.Plane))
                    continue;

                // The catalog may know aircraft or commands the local database does not have yet.
                bool? isAxis;
                if (IsDcs(row.Game))
                {
                    HashSet<string> known = DatabaseIds(dbIds, row.Plane);
                    if (known.Count == 0)
                    {
                        AddUnique(result.AircraftMissingInDatabase, row.Plane);
                        continue;
                    }
                    isAxis = known.Contains(row.Action) ? IsAxisId(row.Action) : (bool?)null;
                }
                else
                {
                    isAxis = GameCommandAxis(row.Game, row.Action);
                }
                if (!isAxis.HasValue)
                {
                    result.CommandsMissingInDatabase++;
                    continue;
                }
                if (isAxis.Value != slot.Axis)
                {
                    AddUnique(result.Warnings, "Skipped " + row.Plane + " " + row.Action + ": axis/button does not match slot '" + slot.Name + "'.");
                    continue;
                }
                usable.Add(row);
            }
            if (useMap)
                result.MapRows = AddMapRows(slots, aircraft, usable, result);

            // A plane's own functions (manual-verified) versus the catalog's fallback substitutes.
            Dictionary<string, Dictionary<string, List<string>>> own = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, List<string>>> substitutes = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> functionsOf = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (Row row in usable)
            {
                bool substitute = string.Equals(row.Status, "substitute", StringComparison.OrdinalIgnoreCase);
                AddToIndex(substitute ? substitutes : own, row.Plane, row.Slot, row.Action);
                if (!substitute)
                {
                    HashSet<string> functions;
                    if (!functionsOf.TryGetValue(row.Plane + "|" + row.Action, out functions))
                        functionsOf[row.Plane + "|" + row.Action] = functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    functions.Add(row.Slot);
                }
            }

            Dictionary<string, Relation> relations = InternalDataManagement.AllRelations;
            Dictionary<string, List<KeyValuePair<string, int>>> learned;
            Dictionary<string, string> learnedNames;
            LearnStyle(relations, slots, aircraft, own, functionsOf, rejected, result, out learned, out learnedNames);

            Dictionary<string, Group> groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Aircraft> plane in aircraft.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (filter.Count > 0 && !filter.Contains(plane.Key) && !filter.Contains(plane.Value.VariantOf))
                    continue;
                foreach (Slot slot in slots.Values)
                {
                    List<string> actions = Lookup(own, plane.Key, slot.Id);
                    string status = "own";
                    if (actions.Count == 0 && includeSubstitutes && slot.Substitutable)
                    {
                        // The user's own habit first: what the user puts on this function when an aircraft lacks it.
                        List<KeyValuePair<string, int>> habits;
                        if (learned.TryGetValue(slot.Id, out habits))
                        {
                            foreach (KeyValuePair<string, int> habit in habits)
                            {
                                actions = Lookup(own, plane.Key, habit.Key);
                                if (actions.Count > 0)
                                {
                                    status = "learned";
                                    break;
                                }
                            }
                        }
                        if (actions.Count == 0)
                        {
                            actions = Lookup(substitutes, plane.Key, slot.Id);
                            status = "substitute";
                        }
                    }
                    if (actions.Count == 0)
                        continue;

                    string target;
                    string suffix = Suffix(preset, slot, plane.Key, plane.Value);
                    // A new relation gets a readable name ("AntiSkidOff" -> "Anti Skid Off", "ai_menu_down" -> "AI menu
                    // down"); a relation of the catalog's own spelling (an earlier Auto Group) keeps being used.
                    string slotName = relations.ContainsKey(slot.Name + suffix) ? slot.Name : ReadableName(slot.Name);
                    string name = preset == "all" && !relations.ContainsKey(slot.Name) && !relations.ContainsKey(slotName) && learnedNames.TryGetValue(slot.Id, out target)
                        ? target
                        : slotName + suffix;
                    Group group;
                    if (!groups.TryGetValue(name, out group))
                    {
                        group = new Group { Slot = slot, Name = name };
                        groups[name] = group;
                    }
                    foreach (string action in actions)
                        group.Rows.Add(new Row { Game = plane.Value.Game, Plane = plane.Key, Slot = slot.Id, Action = action, Status = status });
                    if (status == "learned")
                        result.LearnedSubstitutes++;
                }
            }

            HashSet<string> removedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (replace)
            {
                foreach (KeyValuePair<string, Relation> pair in relations.ToList())
                {
                    if (pair.Value == null || pair.Value.Groups == null || !pair.Value.Groups.Contains(GroupTag, StringComparer.OrdinalIgnoreCase))
                        continue;
                    removedNames.Add(pair.Key);
                    if (!dryRun)
                    {
                        relations.Remove(pair.Key);
                        InternalDataManagement.AllBinds.Remove(pair.Key);
                    }
                }
                result.RelationsRemoved = removedNames.Count;
            }

            // A command a plane already uses in one of the user's other relations stays there: binding it twice
            // would make two relations fight over the same DCS command.
            Dictionary<string, List<string>> usedElsewhere = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            // A command the user switched off for a plane stays off: Auto Group never re-enables it.
            Dictionary<string, List<string>> disabledByUser = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> pair in relations)
            {
                if (pair.Value == null || pair.Value.NODES == null || removedNames.Contains(pair.Key))
                    continue;
                foreach (RelationItem node in pair.Value.NODES)
                {
                    if (node == null || node.AIRCRAFT == null)
                        continue;
                    foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT)
                    {
                        AddOwner(plane.Value ? usedElsewhere : disabledByUser, NodeKey(node.Game, node.ID, plane.Key), pair.Key);
                    }
                }
            }

            HashSet<string> planesCovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            EngineFunctionMap.Map functionMap = useMap ? EngineFunctionMap.Load(result.Warnings) : null;
            foreach (Group group in groups.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                Relation relation;
                bool exists = relations.TryGetValue(group.Name, out relation) && relation != null && !removedNames.Contains(group.Name);
                if (exists && relation.ISAXIS != group.Slot.Axis)
                {
                    AddUnique(result.Warnings, "Relation '" + group.Name + "' exists with a different axis/button type; left unchanged.");
                    continue;
                }
                if (!exists)
                {
                    relation = new Relation();
                    relation.NAME = group.Name;
                    relation.ISAXIS = group.Slot.Axis;
                    relation.Groups = new List<string> { GroupTag };
                    relation.NODES = new List<RelationItem>();
                }

                int added = 0;
                // A relation the user made (not one Auto Group made): only what is that function goes in, no axis that
                // only the function catalog reads as it (a gunner seat's "Gun Up/Down" in the user's "Pitch").
                bool userRelation = exists && (relation.Groups == null || !relation.Groups.Contains(GroupTag, StringComparer.OrdinalIgnoreCase));
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Per id: aircraft whose meaning of it differs from the node's other aircraft (and that the function
                // map does not read as the same function) are left out, switched off and listed.
                HashSet<string> meaningOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (IGrouping<string, Row> sameId in group.Rows.Where(r => IsDcs(r.Game)).GroupBy(r => r.Action, StringComparer.OrdinalIgnoreCase))
                {
                    List<string> excluded;
                    List<string> notes;
                    EngineIdMeanings.Guard("DCS", sameId.Key, sameId.Select(r => r.Plane), group.Name, out excluded, out notes);
                    foreach (string plane in excluded)
                        meaningOut.Add(NodeKey("DCS", sameId.Key, plane));
                    foreach (string note in notes)
                        AddUnique(result.MeaningNotes, "'" + group.Name + "': " + note);
                }
                foreach (Row row in group.Rows)
                {
                    string key = NodeKey(row.Game, row.Action, row.Plane);
                    if (!seen.Add(key))
                        continue;
                    if (meaningOut.Contains(key))
                    {
                        result.MeaningExcluded++;
                        if (!dryRun && !usedElsewhere.ContainsKey(key) && relation.AddNode(row.Action, row.Game, relation.ISAXIS, row.Plane))
                        {
                            RelationItem node = relation.NODES.FirstOrDefault(n => n != null && string.Equals(n.ID, row.Action, StringComparison.OrdinalIgnoreCase) &&
                                                                                   string.Equals(EngineRelationSuggest.GameKey(n.Game ?? string.Empty), "DCS", StringComparison.OrdinalIgnoreCase));
                            if (node != null)
                                node.SetAircraftActivity(row.Plane, false);
                        }
                        continue;
                    }
                    if (userRelation && group.Slot.Axis && CatalogOnlyAxis(functionMap, row, group.Slot.Id))
                    {
                        result.KeptOutOfOwnRelations++;
                        AddUnique(result.Kept, row.Plane + " " + row.Action + " not added to your '" + group.Name + "': only the function catalog reads it as this function, its title does not say so");
                        continue;
                    }
                    List<string> owners;
                    if (usedElsewhere.TryGetValue(key, out owners))
                    {
                        if (!owners.Contains(group.Name, StringComparer.OrdinalIgnoreCase))
                        {
                            result.KeptInOtherRelations++;
                            AddUnique(result.Kept, row.Plane + " " + row.Action + " stays in '" + string.Join("', '", owners) + "' (catalog: '" + group.Name + "')");
                        }
                        continue;
                    }
                    if (disabledByUser.TryGetValue(key, out owners))
                    {
                        result.KeptDisabled++;
                        AddUnique(result.Kept, row.Plane + " " + row.Action + " stays disabled in '" + string.Join("', '", owners) + "' (catalog: '" + group.Name + "')");
                        continue;
                    }
                    if (!dryRun && !relation.AddNode(EngineRelationSuggest.NodeId(EngineRelationSuggest.GameKey(row.Game), row.Action), row.Game, relation.ISAXIS, row.Plane))
                        continue;
                    added++;
                    planesCovered.Add(row.Plane);
                }

                if (added == 0)
                    continue;
                if (!exists)
                {
                    if (!dryRun)
                        relations[relation.NAME] = relation;
                    result.RelationsCreated++;
                }
                else
                {
                    result.RelationsExtended++;
                }
                result.NodesAdded += added;
                result.Relations.Add(new EngineAutoGroupRelation
                {
                    Name = group.Name,
                    Slot = group.Slot.Id,
                    Kind = group.Slot.Kind,
                    IsAxis = group.Slot.Axis,
                    Existing = exists,
                    NodesAdded = added,
                    Aircraft = group.Rows.Select(r => r.Plane).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
                });
            }

            result.AircraftCovered = planesCovered.Count;
            return result;
        }

        // DCS layers and seats that are no aircraft of their own: the map gives them no relations.
        private static readonly string[] NoAircraftMarks = { "Camera", "Menu", "UiLayer", "_UI", "TrackIR", "AimingStation", "VoiceChat", "RadioVoIP", "Personal Weapon" };

        // Rows from the function map for every action the catalog does not list: each (function, direction) goes to the
        // slot the catalog uses most for it (the user's style, learned from the catalog's own rows), only where the plane
        // has no catalog row for that slot, one action per plane and slot (the clearest title wins). Aircraft and games
        // the catalog does not know join with their class (helicopter, spaceship, else fixed wing).
        // Also used by the Setup wizard (EngineSetupWizard.Request) for the same coverage as Auto Group.
        internal static int AddMapRows(Dictionary<string, Slot> slots, Dictionary<string, Aircraft> aircraft, List<Row> usable, EngineAutoGroupResult result)
        {
            EngineFunctionMap.Map map = EngineFunctionMap.Load(result.Warnings);
            if (map.Functions.Count == 0)
                return 0;
            Dictionary<string, Dictionary<string, int>> slotVotes = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Row row in usable)
            {
                string game = EngineRelationSuggest.GameKey(row.Game);
                string plane = game == "DCS" ? row.Plane : game;
                string action = EngineRelationSuggest.NodeId(game, row.Action);
                covered.Add(plane + "|" + row.Slot);
                known.Add(game + "|" + action + "|" + plane);
                if (string.Equals(row.Status, "substitute", StringComparison.OrdinalIgnoreCase))
                    continue;
                EngineFunctionMap.Entry entry;
                if (!map.ByKey.TryGetValue(game + "|" + action + "|" + plane, out entry))
                    continue;
                foreach (EngineFunctionMap.Assignment a in entry.Functions.Where(x => x.Source == "catalog" && x.Function.Slots.Any(l => string.Equals(l.Slot, row.Slot, StringComparison.OrdinalIgnoreCase))))
                {
                    string key = a.Function.Id + "|" + a.Dir;
                    Dictionary<string, int> votes;
                    if (!slotVotes.TryGetValue(key, out votes))
                        slotVotes[key] = votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    votes[row.Slot] = (votes.ContainsKey(row.Slot) ? votes[row.Slot] : 0) + 1;
                }
            }
            Dictionary<string, int> actionsPerPlane = map.Entries.GroupBy(e => e.Command.Plane).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, KeyValuePair<EngineFunctionMap.Entry, double>> best = new Dictionary<string, KeyValuePair<EngineFunctionMap.Entry, double>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> slotOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (EngineFunctionMap.Entry entry in map.Entries)
            {
                EngineRelationSuggest.Command c = entry.Command;
                string plane = c.Game == "DCS" ? c.Plane : c.Game;
                if (known.Contains(c.Game + "|" + c.Id + "|" + plane))
                    continue;
                if (!aircraft.ContainsKey(plane) && (map.AutoGroupIgnore.Contains(plane) ||
                                                     (c.Game == "DCS" && (NoAircraftMarks.Any(m => plane.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) || actionsPerPlane[c.Plane] < 300))))
                    continue;
                foreach (EngineFunctionMap.Assignment a in entry.Functions.Where(x => x.Source == "rule" && x.Weight >= 0.9 && x.InTitle))
                {
                    Dictionary<string, int> votes;
                    if (!slotVotes.TryGetValue(a.Function.Id + "|" + a.Dir, out votes) || votes.Count == 0)
                        continue;
                    string slotId = votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal).First().Key;
                    Slot slot;
                    if (!slots.TryGetValue(slotId, out slot) || slot.Axis != c.IsAxis || slot.Scope == "aircraft" || slot.Kind == "mfd-osb" || slot.Kind == "ufc-key")
                        continue;
                    string key = plane + "|" + slotId;
                    if (covered.Contains(key))
                        continue;
                    // The clearest title wins: full weight, few words.
                    double score = a.Weight * 10 - entry.TitleTokens.Length * 0.1;
                    KeyValuePair<EngineFunctionMap.Entry, double> current;
                    if (!best.TryGetValue(key, out current) || current.Value < score)
                    {
                        best[key] = new KeyValuePair<EngineFunctionMap.Entry, double>(entry, score);
                        slotOf[key] = slotId;
                    }
                }
            }
            int added = 0;
            // Aircraft and games with no row of their own in the tables: joined by the map (also a game that has an
            // aircraft.json entry but no table, e.g. Tiny Combat Arena).
            HashSet<string> ownPlanes = new HashSet<string>(usable.Select(r => EngineRelationSuggest.GameKey(r.Game) == "DCS" ? r.Plane : EngineRelationSuggest.GameKey(r.Game)), StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, KeyValuePair<EngineFunctionMap.Entry, double>> pick in best.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                EngineRelationSuggest.Command c = pick.Value.Key.Command;
                string plane = c.Game == "DCS" ? c.Plane : c.Game;
                if (!aircraft.ContainsKey(plane))
                {
                    string cls = pick.Value.Key.Class;
                    aircraft[plane] = new Aircraft
                    {
                        Game = c.Game,
                        Type = cls == "heli" ? "rotary_wing" : cls == "space" ? "spaceship" : "fixed_wing",
                        Era = string.Empty
                    };
                }
                if (!ownPlanes.Contains(plane))
                    AddUnique(result.MapAircraft, plane);
                usable.Add(new Row { Game = c.Game, Plane = plane, Slot = slotOf[pick.Key], Action = c.Id, Status = "map" });
                added++;
            }
            return added;
        }

        // The user's substitution habits (function -> the functions the user puts there when an aircraft lacks it, with
        // the number of separate decisions), learned like Run does, for the relation editor's suggestions. rules gets the
        // readable form ("CMS FWD <- TMS Up (A-10C, F-16C_50)").
        internal static Dictionary<string, List<KeyValuePair<string, int>>> LearnHabits(
            Dictionary<string, Slot> slots, Dictionary<string, Aircraft> aircraft, List<Row> rows, List<string> rules)
        {
            string folder = EngineNlGroup.FunctionsFolder();
            HashSet<string> rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(folder))
            {
                foreach (string file in Directory.GetFiles(folder, "*.rejected.tsv"))
                    rejected.UnionWith(LoadRejected(file));
            }
            Dictionary<string, Dictionary<string, List<string>>> own = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> functionsOf = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (Row row in rows)
            {
                if (string.Equals(row.Status, "substitute", StringComparison.OrdinalIgnoreCase) || string.Equals(row.Status, "rejected", StringComparison.OrdinalIgnoreCase))
                    continue;
                AddToIndex(own, row.Plane, row.Slot, row.Action);
                HashSet<string> functions;
                if (!functionsOf.TryGetValue(row.Plane + "|" + row.Action, out functions))
                    functionsOf[row.Plane + "|" + row.Action] = functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                functions.Add(row.Slot);
            }
            EngineAutoGroupResult result = new EngineAutoGroupResult();
            Dictionary<string, List<KeyValuePair<string, int>>> learned;
            Dictionary<string, string> learnedNames;
            LearnStyle(InternalDataManagement.AllRelations, slots, aircraft, own, functionsOf, rejected, result, out learned, out learnedNames);
            if (rules != null)
                rules.AddRange(result.LearnedRules);
            return learned;
        }

        // Learns the user's style from the relations the user built (AutoGroup relations are ignored):
        // - which catalog function a relation stands for (the function most of its commands have), and
        // - which other function the user puts there for aircraft that lack it (substitution habits).
        private static void LearnStyle(
            Dictionary<string, Relation> relations,
            Dictionary<string, Slot> slots,
            Dictionary<string, Aircraft> aircraft,
            Dictionary<string, Dictionary<string, List<string>>> own,
            Dictionary<string, HashSet<string>> functionsOf,
            HashSet<string> rejected,
            EngineAutoGroupResult result,
            out Dictionary<string, List<KeyValuePair<string, int>>> learned,
            out Dictionary<string, string> learnedNames)
        {
            // function -> substitute function -> evidence. JoyPro copies one command id to every aircraft that has it,
            // so a habit is counted in distinct commands (separate decisions of the user), not in aircraft.
            Dictionary<string, Dictionary<string, Habit>> pairs = new Dictionary<string, Dictionary<string, Habit>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, KeyValuePair<string, int>> bestRelation = new Dictionary<string, KeyValuePair<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> pair in relations)
            {
                Relation relation = pair.Value;
                if (relation == null || relation.NODES == null ||
                    (relation.Groups != null && relation.Groups.Contains(GroupTag, StringComparer.OrdinalIgnoreCase)))
                    continue;
                // The user said the relation has no function (a button position): nothing to learn from it.
                if (string.Equals(relation.Function, EngineRelationPurpose.NoneValue, StringComparison.OrdinalIgnoreCase))
                    continue;

                List<StyleEntry> entries = new List<StyleEntry>();
                foreach (RelationItem node in relation.NODES)
                {
                    if (node == null || node.AIRCRAFT == null)
                        continue;
                    foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT)
                    {
                        HashSet<string> functions;
                        if (plane.Value && functionsOf.TryGetValue(plane.Key + "|" + node.ID, out functions))
                            entries.Add(new StyleEntry { Plane = plane.Key, Action = node.ID, Functions = functions });
                    }
                }
                if (entries.Count < 2)
                    continue;

                Dictionary<string, int> votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (StyleEntry entry in entries)
                {
                    foreach (string function in entry.Functions)
                        votes[function] = (votes.ContainsKey(function) ? votes[function] : 0) + 1;
                }
                KeyValuePair<string, int> dominant = votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal).First();
                // The function the user picked for it wins over what most of its commands are.
                if (!string.IsNullOrWhiteSpace(relation.Function) && slots.ContainsKey(relation.Function.Trim()))
                    dominant = new KeyValuePair<string, int>(relation.Function.Trim(), Math.Max(2, entries.Count));
                Slot slot;
                if (dominant.Value < 2 || dominant.Value * 2 < entries.Count || !slots.TryGetValue(dominant.Key, out slot) || slot.Axis != relation.ISAXIS)
                    continue;
                if (!slot.Substitutable)
                {
                    RememberRelation(bestRelation, slot.Id, relation.NAME, dominant.Value);
                    continue;
                }

                RememberRelation(bestRelation, slot.Id, relation.NAME, dominant.Value);

                // Commands the manuals proved wrong here (old placeholders) are no habit, on any aircraft they were copied to.
                HashSet<string> rejectedActions = new HashSet<string>(
                    entries.Where(e => rejected.Contains(e.Plane + "|" + slot.Id + "|" + e.Action)).Select(e => e.Action),
                    StringComparer.Ordinal);
                foreach (StyleEntry entry in entries)
                {
                    if (entry.Functions.Contains(slot.Id) || Lookup(own, entry.Plane, slot.Id).Count > 0 || rejectedActions.Contains(entry.Action))
                        continue;
                    Dictionary<string, Habit> habit;
                    if (!pairs.TryGetValue(slot.Id, out habit))
                        pairs[slot.Id] = habit = new Dictionary<string, Habit>(StringComparer.OrdinalIgnoreCase);
                    Aircraft info;
                    string baseAircraft = aircraft.TryGetValue(entry.Plane, out info) && !string.IsNullOrWhiteSpace(info.VariantOf) ? info.VariantOf : entry.Plane;
                    foreach (string function in entry.Functions)
                    {
                        Slot source;
                        if (!slots.TryGetValue(function, out source) || !source.Substitutable || IsPlaceholderSource(source, slot))
                            continue;
                        Habit evidence;
                        if (!habit.TryGetValue(function, out evidence))
                            habit[function] = evidence = new Habit();
                        evidence.Aircraft.Add(baseAircraft);
                        evidence.Commands.Add(entry.Action);
                    }
                }
            }

            learned = new Dictionary<string, List<KeyValuePair<string, int>>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Dictionary<string, Habit>> pair in pairs)
            {
                // One odd entry is no habit: a substitution counts once the user made it with two different commands.
                List<KeyValuePair<string, Habit>> habits = pair.Value.Where(v => v.Value.Commands.Count >= MinHabit)
                    .OrderByDescending(v => v.Value.Commands.Count).ThenBy(v => v.Key, StringComparer.Ordinal).ToList();
                learned[pair.Key] = habits.Select(h => new KeyValuePair<string, int>(h.Key, h.Value.Commands.Count)).ToList();
                foreach (KeyValuePair<string, Habit> habit in habits)
                {
                    Slot from;
                    Slot to;
                    if (slots.TryGetValue(pair.Key, out to) && slots.TryGetValue(habit.Key, out from))
                        result.LearnedRules.Add(to.Name + " <- " + from.Name + " (" + string.Join(", ", habit.Value.Aircraft.OrderBy(v => v, StringComparer.OrdinalIgnoreCase)) + ")");
                }
            }

            // A relation of the user that stands for a function keeps receiving it, whatever the user named it.
            learnedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, KeyValuePair<string, int>> pair in bestRelation.OrderByDescending(p => p.Value.Value))
            {
                if (claimed.Add(pair.Value.Key))
                    learnedNames[pair.Key] = pair.Value.Key;
            }
        }

        private sealed class Habit
        {
            public HashSet<string> Aircraft = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Commands = new HashSet<string>(StringComparer.Ordinal);
        }

        private sealed class StyleEntry
        {
            public string Plane;
            public string Action;
            public HashSet<string> Functions;
        }

        // Kneeboard commands were parked on unrelated slots in old profiles; that is no substitution habit.
        private static bool IsPlaceholderSource(Slot source, Slot target)
        {
            return source.Id.StartsWith("kneeboard", StringComparison.OrdinalIgnoreCase) &&
                   !target.Id.StartsWith("kneeboard", StringComparison.OrdinalIgnoreCase);
        }

        private static HashSet<string> LoadRejected(string file)
        {
            HashSet<string> rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(file))
                return rejected;
            foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;
                string[] parts = line.Split('\t');
                if (parts.Length >= 3)
                    rejected.Add(parts[0] + "|" + parts[1] + "|" + parts[2]);
            }
            return rejected;
        }

        private static void RememberRelation(Dictionary<string, KeyValuePair<string, int>> best, string slot, string relation, int votes)
        {
            KeyValuePair<string, int> current;
            if (!best.TryGetValue(slot, out current) || votes > current.Value)
                best[slot] = new KeyValuePair<string, int>(relation, votes);
        }

        private static void AddToIndex(Dictionary<string, Dictionary<string, List<string>>> index, string plane, string slot, string action)
        {
            Dictionary<string, List<string>> bySlot;
            if (!index.TryGetValue(plane, out bySlot))
                index[plane] = bySlot = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            List<string> actions;
            if (!bySlot.TryGetValue(slot, out actions))
                bySlot[slot] = actions = new List<string>();
            if (!actions.Contains(action))
                actions.Add(action);
        }

        private static List<string> Lookup(Dictionary<string, Dictionary<string, List<string>>> index, string plane, string slot)
        {
            Dictionary<string, List<string>> bySlot;
            List<string> actions;
            return index.TryGetValue(plane, out bySlot) && bySlot.TryGetValue(slot, out actions) ? actions : new List<string>();
        }

        internal static string Suffix(string preset, Slot slot, string planeName, Aircraft plane)
        {
            // Input layers (Default, NS430, VoIP ...) and ground units serve every aircraft: never split by wing or era.
            bool sharedLayer = plane.Type == "layer" || plane.Type == "ground";
            string type = plane.Type == "spaceship" ? "Spaceship" : plane.Type == "rotary_wing" ? "Rotary wing" : "Fixed wing";
            switch (preset)
            {
                case "wing":
                    return sharedLayer ? string.Empty : " [" + type + "]";
                case "era":
                    if (sharedLayer)
                        return string.Empty;
                    return plane.Type == "spaceship" ? " [" + type + "]" : " [" + type + ", " + EraName(plane.Era) + "]";
                case "purist":
                    if (slot.Basic)
                        return string.Empty;
                    return " [" + (string.IsNullOrWhiteSpace(plane.VariantOf) ? planeName : plane.VariantOf) + "]";
                default:
                    return string.Empty;
            }
        }

        private static string EraName(string era)
        {
            switch ((era ?? string.Empty).ToLowerInvariant())
            {
                case "ww2": return "WW2";
                case "korea": return "Korea";
                case "cold_war_early": return "Early Cold War";
                case "cold_war_late": return "Late Cold War";
                case "modern": return "Modern";
                default: return "Other";
            }
        }

        internal static HashSet<string> DatabaseIds(Dictionary<string, HashSet<string>> cache, string plane)
        {
            HashSet<string> ids;
            if (cache.TryGetValue(plane, out ids))
                return ids;
            ids = new HashSet<string>(StringComparer.Ordinal);
            string file = GameDataPaths.DbFile("DCS", plane + ".html");
            if (File.Exists(file))
            {
                foreach (DCSInput input in InternalDataManagement.ParseDcsHtmlInputs(file, plane))
                {
                    if (input != null && !string.IsNullOrWhiteSpace(input.ID))
                        ids.Add(input.ID);
                }
            }
            cache[plane] = ids;
            return ids;
        }

        private static string NodeKey(string game, string id, string plane)
        {
            string key = IsDcs(game) ? "DCS" : EngineRelationSuggest.GameKey(game);
            return key + "|" + EngineRelationSuggest.NodeId(key, id) + "|" + plane;
        }

        // An axis whose title has none of the words of the functions this catalog slot stands for (names, synonyms, game
        // terms): only the catalog reads it as the function. False when the map does not know the action or the slot.
        private static bool CatalogOnlyAxis(EngineFunctionMap.Map map, Row row, string slot)
        {
            if (map == null)
                return false;
            string game = EngineRelationSuggest.GameKey(row.Game);
            string plane = game == "DCS" ? row.Plane : game;
            EngineFunctionMap.Entry entry;
            if (!map.ByKey.TryGetValue(game + "|" + EngineRelationSuggest.NodeId(game, row.Action) + "|" + plane, out entry) || entry.TitleTokens.Length == 0)
                return false;
            List<EngineFunctionMap.Function> functions = map.Functions.Where(f => f.AxisKind && f.Slots.Any(l => string.Equals(l.Slot, slot, StringComparison.OrdinalIgnoreCase))).ToList();
            return functions.Count > 0 && !functions.Any(f => EngineRelationSearch.TitleHasPhrase(entry, f));
        }

        // "AntiSkidOff" -> "Anti Skid Off", "TGPCameraON" -> "TGP Camera ON", "ai_menu_down" -> "AI menu down"; a name with
        // spaces stays as it is.
        internal static string ReadableName(string name)
        {
            string text = (name ?? string.Empty).Trim();
            if (text.Length == 0 || text.IndexOf(' ') >= 0)
                return text;
            text = text.Replace('_', ' ');
            text = System.Text.RegularExpressions.Regex.Replace(text, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
            string[] words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                if (ReadableAcronyms.Contains(words[i]))
                    words[i] = words[i].ToUpperInvariant();
            }
            text = string.Join(" ", words);
            return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        private static readonly HashSet<string> ReadableAcronyms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ai", "ap", "apu", "atc", "hud", "mfd", "ufc", "tgp", "fcs", "ins", "nvg", "ptt", "tms", "dms", "cms", "ecm", "rwr", "ils", "iff"
        };

        // Commands of the other games: their own catalog (Star Citizen, IL-2, registry games) says whether the command
        // exists and whether it is an axis. Null = unknown to the local database.
        internal static bool? GameCommandAxis(string game, string id)
        {
            try
            {
                return Program.TryInferNodeAxis(game, id);
            }
            catch
            {
                return null;
            }
        }

        // DCS command ids: axis commands start with 'a', buttons with 'd'.
        private static bool IsAxisId(string id)
        {
            return !string.IsNullOrEmpty(id) && (id[0] == 'a' || id[0] == 'A');
        }

        internal static bool IsDcs(string game)
        {
            return string.IsNullOrWhiteSpace(game) || string.Equals(game, "DCS", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryLoadCatalog(string folder, EngineAutoGroupResult result, out Dictionary<string, Slot> slots, out Dictionary<string, Aircraft> aircraft, out List<Row> rows)
        {
            if (!TryLoadCatalog(folder, result, out slots, out aircraft, out rows, false))
                return false;
            // Games without a table of their own get one from the function map (EngineFunctionTable), so every game
            // reaches search by function, suggestions, Auto Assign and the AI paths.
            EngineFunctionTable.AddDerived(folder, slots, aircraft, rows);
            return true;
        }

        // ownOnly: only the files (the function map reads this; Auto Group uses the map itself for the rest).
        internal static bool TryLoadCatalog(string folder, EngineAutoGroupResult result, out Dictionary<string, Slot> slots, out Dictionary<string, Aircraft> aircraft, out List<Row> rows, bool ownOnly)
        {
            slots = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
            aircraft = new Dictionary<string, Aircraft>(StringComparer.OrdinalIgnoreCase);
            rows = new List<Row>();
            string slotsFile = Path.Combine(folder, "slots.json");
            string aircraftFile = Path.Combine(folder, "aircraft.json");
            foreach (string file in new[] { slotsFile, aircraftFile, Path.Combine(folder, "DCS.tsv") })
            {
                if (!File.Exists(file))
                {
                    result.Errors.Add("Function catalog file missing: " + file + " (update JoyPro).");
                    return false;
                }
            }

            try
            {
                foreach (Dictionary<string, object> item in EngineSerialization.FromJson<List<Dictionary<string, object>>>(File.ReadAllText(slotsFile, Encoding.UTF8)))
                {
                    Slot slot = new Slot
                    {
                        Id = Text(item, "id"),
                        Name = Text(item, "name"),
                        Kind = Text(item, "kind"),
                        Axis = Flag(item, "axis"),
                        Basic = Flag(item, "basic"),
                        Scope = Text(item, "scope")
                    };
                    if (slot.Id.Length > 0 && slot.Name.Length > 0)
                        slots[slot.Id] = slot;
                }
                foreach (KeyValuePair<string, Dictionary<string, object>> item in EngineSerialization.FromJson<Dictionary<string, Dictionary<string, object>>>(File.ReadAllText(aircraftFile, Encoding.UTF8)))
                {
                    string game = Text(item.Value, "game");
                    aircraft[item.Key] = new Aircraft
                    {
                        Game = game.Length > 0 ? game : "DCS",
                        Type = Text(item.Value, "type"),
                        Era = Text(item.Value, "era"),
                        VariantOf = Text(item.Value, "variant_of")
                    };
                }
                foreach (string mappingFile in Directory.GetFiles(folder, "*.tsv"))
                {
                    if (mappingFile.EndsWith(".rejected.tsv", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string game = Path.GetFileNameWithoutExtension(mappingFile);
                    foreach (string line in File.ReadAllLines(mappingFile, Encoding.UTF8))
                    {
                        if (line.Length == 0 || line[0] == '#')
                            continue;
                        string[] parts = line.Split('\t');
                        if (parts.Length >= 4)
                            rows.Add(new Row { Game = game, Plane = parts[0], Slot = parts[1], Action = parts[2], Status = parts[3] });
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add("Function catalog could not be read: " + ex.Message);
                return false;
            }

            result.CatalogAircraft = aircraft.Count;
            result.CatalogSlots = slots.Count;
            return true;
        }

        private static string Text(Dictionary<string, object> item, string key)
        {
            object value;
            return item != null && item.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture).Trim() : string.Empty;
        }

        private static bool Flag(Dictionary<string, object> item, string key)
        {
            return string.Equals(Text(item, key), "true", StringComparison.OrdinalIgnoreCase);
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

        public static string ToText(EngineAutoGroupResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Auto Group");
            sb.AppendLine("Preset: " + result.Preset);
            sb.AppendLine("Include Substitutes: " + result.IncludeSubstitutes);
            sb.AppendLine("Dry Run: " + result.DryRun);
            sb.AppendLine("Catalog: " + result.CatalogSlots.ToString(CultureInfo.InvariantCulture) + " slots, " + result.CatalogAircraft.ToString(CultureInfo.InvariantCulture) + " aircraft");
            sb.AppendLine("Relations Removed: " + result.RelationsRemoved.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Relations Created: " + result.RelationsCreated.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Relations Extended: " + result.RelationsExtended.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Nodes Added: " + result.NodesAdded.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Kept In Other Relations: " + result.KeptInOtherRelations.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Kept Disabled By You: " + result.KeptDisabled.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Left Out, Same Id Means Something Else: " + result.MeaningExcluded.ToString(CultureInfo.InvariantCulture));
            foreach (string note in result.MeaningNotes.Take(20))
                sb.AppendLine("  ~ " + note);
            sb.AppendLine("Aircraft Covered: " + result.AircraftCovered.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Substitutes From Your Style: " + result.LearnedSubstitutes.ToString(CultureInfo.InvariantCulture));
            if (result.UseMap)
                sb.AppendLine("Function Map: " + result.MapRows.ToString(CultureInfo.InvariantCulture) + " actions the catalog does not list" +
                              (result.MapAircraft.Count > 0 ? "; aircraft and games: " + string.Join(", ", result.MapAircraft) : string.Empty));
            if (result.AircraftMissingInDatabase.Count > 0 || result.CommandsMissingInDatabase > 0)
            {
                sb.AppendLine("Aircraft Missing In Database: " + string.Join(", ", result.AircraftMissingInDatabase));
                sb.AppendLine("Commands Missing In Database: " + result.CommandsMissingInDatabase.ToString(CultureInfo.InvariantCulture) + " (refresh the DCS database to include them)");
            }
            sb.AppendLine("Relations:");
            foreach (EngineAutoGroupRelation relation in result.Relations)
            {
                sb.AppendLine("  - " + relation.Name + " (" + (relation.IsAxis ? "axis" : "button") + ", " +
                              (relation.Existing ? "extended" : "new") + ", +" + relation.NodesAdded.ToString(CultureInfo.InvariantCulture) +
                              " nodes): " + string.Join(", ", relation.Aircraft));
            }
            if (result.LearnedRules.Count > 0)
            {
                sb.AppendLine("Learned From Your Profile (function <- what you use when an aircraft lacks it):");
                foreach (string rule in result.LearnedRules)
                    sb.AppendLine("  - " + rule);
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
    }

    public sealed class EngineAutoGroupRelation
    {
        public string Name { get; set; }
        public string Slot { get; set; }
        public string Kind { get; set; }
        public bool IsAxis { get; set; }
        public bool Existing { get; set; }
        public int NodesAdded { get; set; }
        public List<string> Aircraft { get; set; }
    }

    public sealed class EngineAutoGroupResult
    {
        public string Preset { get; set; }
        public bool IncludeSubstitutes { get; set; }
        public bool Replace { get; set; }
        public bool DryRun { get; set; }
        public int CatalogSlots { get; set; }
        public int CatalogAircraft { get; set; }
        public int RelationsRemoved { get; set; }
        public int RelationsCreated { get; set; }
        public int RelationsExtended { get; set; }
        public int NodesAdded { get; set; }
        public int KeptInOtherRelations { get; set; }
        // Axes left out of the user's own relations: only the function catalog reads them as the relation's function.
        public int KeptOutOfOwnRelations { get; set; }
        public int KeptDisabled { get; set; }
        public int AircraftCovered { get; set; }
        public int CommandsMissingInDatabase { get; set; }
        public int LearnedSubstitutes { get; set; }
        // The function map: whether it was used, how many rows it added, the aircraft and games it brought in.
        public bool UseMap { get; set; }
        public int MapRows { get; set; }
        public List<string> MapAircraft { get; set; }
        public List<string> LearnedRules { get; set; }
        public List<string> AircraftMissingInDatabase { get; set; }
        public List<EngineAutoGroupRelation> Relations { get; set; }
        public List<string> Kept { get; set; }
        // Aircraft left out (in the node switched off) because the id means something else there than in the other
        // aircraft of the node (EngineIdMeanings.Guard); one tick in the node's aircraft list re-includes one.
        public int MeaningExcluded { get; set; }
        public List<string> MeaningNotes { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public EngineAutoGroupResult()
        {
            Preset = "all";
            MeaningNotes = new List<string>();
            Relations = new List<EngineAutoGroupRelation>();
            MapAircraft = new List<string>();
            AircraftMissingInDatabase = new List<string>();
            LearnedRules = new List<string>();
            Kept = new List<string>();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }
}
