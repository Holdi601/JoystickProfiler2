using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    // The check's two errors in detail (profile validate, engine relations conflicts; docs/relation-builder.md "The
    // check's errors"):
    //   - one input does two things: the same device input (with the same modifiers) is bound in two relations that
    //     both have an action of the same aircraft switched on, so the game runs both on one press;
    //   - two actions on one input: one relation has two actions of the same aircraft switched on.
    // Each one names the aircraft, the relations with their actions as the game calls them (an action the game's list
    // no longer has is marked unknown) and the validate line it stands for (Message), so the app can show it in words
    // and offer fixes: keep it in one relation (the aircraft switched off in the others), keep one action, keep both
    // (remembered in the profile, the check does not warn again), switch it off everywhere.
    public sealed class EngineConflictAction
    {
        public string Id { get; set; } = string.Empty;
        // The game's title for the aircraft; for an unknown one the name the relation stored (empty: none).
        public string Title { get; set; } = string.Empty;
        public bool Unknown { get; set; }
    }

    public sealed class EngineConflictSide
    {
        public string Relation { get; set; } = string.Empty;
        public List<EngineConflictAction> Actions { get; set; } = new List<EngineConflictAction>();
    }

    public sealed class EngineInputConflict
    {
        // "game:plane§device§input[§modifier...]" (the validate line's key).
        public string Key { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Game { get; set; } = string.Empty;
        public string Plane { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;
        public string Input { get; set; } = string.Empty;
        public List<string> Modifiers { get; set; } = new List<string>();
        public bool IsAxis { get; set; }
        public List<EngineConflictSide> Sides { get; set; } = new List<EngineConflictSide>();
        public bool Kept { get; set; }
    }

    public sealed class EngineActionConflict
    {
        // "game|plane|relation"
        public string Key { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Game { get; set; } = string.Empty;
        public string Plane { get; set; } = string.Empty;
        public string Relation { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;
        public string Input { get; set; } = string.Empty;
        public List<string> Modifiers { get; set; } = new List<string>();
        public List<EngineConflictAction> Actions { get; set; } = new List<EngineConflictAction>();
        public bool Kept { get; set; }
    }

    public sealed class EngineConflictsResult
    {
        public List<EngineInputConflict> InputConflicts { get; set; } = new List<EngineInputConflict>();
        public List<EngineActionConflict> ActionConflicts { get; set; } = new List<EngineActionConflict>();
        public List<EngineDuplicatePlaneName> PlaneNames { get; set; } = new List<EngineDuplicatePlaneName>();
        public int Changed { get; set; }
        public int KeptCount { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    internal static class EngineCheckConflicts
    {
        private const string InputPrefix = "input|";
        private const string ActionPrefix = "active|";

        // A kept entry of these errors (Pr0file.KeptDuplicates holds them next to the kept duplicates).
        internal static bool IsConflictEntry(string entry)
        {
            string e = entry ?? string.Empty;
            return e.StartsWith(InputPrefix, StringComparison.OrdinalIgnoreCase) || e.StartsWith(ActionPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string Game(string game)
        {
            return string.IsNullOrWhiteSpace(game) ? "DCS" : game.Trim();
        }

        private static Bind BindOf(Relation relation, string name)
        {
            Bind bind = relation == null ? null : relation.bind;
            if (bind == null && InternalDataManagement.AllBinds != null)
                InternalDataManagement.AllBinds.TryGetValue(name ?? string.Empty, out bind);
            return bind;
        }

        private static HashSet<string> Kept()
        {
            return new HashSet<string>((InternalDataManagement.KeptDuplicates ?? new List<string>()).Where(IsConflictEntry), StringComparer.OrdinalIgnoreCase);
        }

        private static string InputEntry(EngineInputConflict conflict)
        {
            return InputPrefix + conflict.Key + "|" + string.Join("\t", conflict.Sides.Select(s => s.Relation).OrderBy(r => r, StringComparer.OrdinalIgnoreCase));
        }

        private static string ActionEntry(EngineActionConflict conflict)
        {
            return ActionPrefix + conflict.Key + "|" + string.Join("\t", conflict.Actions.Select(a => a.Id).OrderBy(i => i, StringComparer.OrdinalIgnoreCase));
        }

        // Every input bound in several relations that have an action of the same aircraft on (in the order and with the
        // words of profile validate's "Button Item key found in multiple Relation" lines).
        internal static List<EngineInputConflict> InputConflicts(bool includeKept, bool withTitles)
        {
            Dictionary<string, EngineInputConflict> byKey = new Dictionary<string, EngineInputConflict>(StringComparer.OrdinalIgnoreCase);
            List<EngineInputConflict> order = new List<EngineInputConflict>();
            foreach (KeyValuePair<string, Bind> kvp in InternalDataManagement.AllBinds ?? new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase))
            {
                string relationName = kvp.Key ?? string.Empty;
                Bind bind = kvp.Value;
                if (bind == null)
                    continue;
                Relation relation = bind.Rl;
                if (relation == null && InternalDataManagement.AllRelations != null && InternalDataManagement.AllRelations.ContainsKey(relationName))
                    relation = InternalDataManagement.AllRelations[relationName];
                if (relation == null)
                    continue;
                string resolvedName = string.IsNullOrWhiteSpace(relation.NAME)
                    ? (string.IsNullOrWhiteSpace(relationName) ? "<unnamed relation>" : relationName)
                    : relation.NAME;
                string joystick = bind.Joystick ?? string.Empty;
                string input = relation.ISAXIS ? (bind.JAxis ?? string.Empty) : (bind.JButton ?? string.Empty);
                List<string> reformers = bind.AllReformers ?? new List<string>();
                foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
                {
                    if (node == null || node.AIRCRAFT == null)
                        continue;
                    string game = Game(node.Game);
                    if (string.Equals(game, "StarCitizen", StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (KeyValuePair<string, bool> aircraft in node.AIRCRAFT)
                    {
                        if (!aircraft.Value || string.IsNullOrWhiteSpace(aircraft.Key))
                            continue;
                        StringBuilder key = new StringBuilder();
                        key.Append(game).Append(":").Append(aircraft.Key.Trim()).Append("§").Append(joystick).Append("§").Append(input);
                        foreach (string reformer in reformers)
                            key.Append("§").Append(reformer ?? string.Empty);
                        EngineInputConflict conflict;
                        if (!byKey.TryGetValue(key.ToString(), out conflict))
                        {
                            conflict = new EngineInputConflict
                            {
                                Key = key.ToString(), Game = game, Plane = aircraft.Key.Trim(), Device = joystick, Input = input, IsAxis = relation.ISAXIS,
                                Modifiers = reformers.Select(ModifierName).Where(m => m.Length > 0).ToList()
                            };
                            byKey[conflict.Key] = conflict;
                            order.Add(conflict);
                        }
                        EngineConflictSide side = conflict.Sides.FirstOrDefault(s => string.Equals(s.Relation, resolvedName, StringComparison.OrdinalIgnoreCase));
                        if (side == null)
                            conflict.Sides.Add(side = new EngineConflictSide { Relation = resolvedName });
                        if (withTitles && !side.Actions.Any(a => string.Equals(a.Id, node.ID, StringComparison.OrdinalIgnoreCase)))
                            side.Actions.Add(ActionOf(game, node.ID, conflict.Plane, node));
                    }
                }
            }
            HashSet<string> kept = Kept();
            List<EngineInputConflict> result = new List<EngineInputConflict>();
            foreach (EngineInputConflict conflict in order.Where(c => c.Sides.Count > 1))
            {
                StringBuilder message = new StringBuilder();
                message.Append("Button Item key found in multiple Relation: ").Append(conflict.Key).Append(" The Relations it was found in: ");
                message.Append(string.Join(", ", conflict.Sides.Select(s => s.Relation)));
                conflict.Message = message.ToString();
                conflict.Kept = kept.Contains(InputEntry(conflict));
                if (conflict.Kept && !includeKept)
                    continue;
                result.Add(conflict);
            }
            return result;
        }

        // Every relation with more than one action of the same aircraft switched on (profile validate's "has multiple
        // active items" lines).
        internal static List<EngineActionConflict> ActionConflicts(bool includeKept, bool withTitles)
        {
            HashSet<string> kept = Kept();
            List<EngineActionConflict> result = new List<EngineActionConflict>();
            foreach (Relation relation in (InternalDataManagement.AllRelations ?? new Dictionary<string, Relation>()).Values.Where(r => r != null))
            {
                string relationName = string.IsNullOrWhiteSpace(relation.NAME) ? "<unnamed relation>" : relation.NAME;
                Dictionary<string, EngineActionConflict> byPlane = new Dictionary<string, EngineActionConflict>(StringComparer.OrdinalIgnoreCase);
                List<EngineActionConflict> order = new List<EngineActionConflict>();
                foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
                {
                    if (node == null || node.AIRCRAFT == null)
                        continue;
                    string game = Game(node.Game);
                    foreach (KeyValuePair<string, bool> aircraft in node.AIRCRAFT)
                    {
                        if (!aircraft.Value || string.IsNullOrWhiteSpace(aircraft.Key))
                            continue;
                        string plane = aircraft.Key.Trim();
                        EngineActionConflict conflict;
                        if (!byPlane.TryGetValue(game + "§" + plane, out conflict))
                        {
                            conflict = new EngineActionConflict { Key = game + "|" + plane + "|" + relationName, Game = game, Plane = plane, Relation = relationName };
                            byPlane[game + "§" + plane] = conflict;
                            order.Add(conflict);
                        }
                        if (!conflict.Actions.Any(a => string.Equals(a.Id, node.ID, StringComparison.OrdinalIgnoreCase)))
                            conflict.Actions.Add(withTitles ? ActionOf(game, node.ID, plane, node) : new EngineConflictAction { Id = node.ID ?? string.Empty });
                    }
                }
                Bind bind = BindOf(relation, relationName);
                foreach (EngineActionConflict conflict in order.Where(c => c.Actions.Count > 1))
                {
                    conflict.Message = "The game " + conflict.Game + " has multiple active items for the plane " + conflict.Plane + " on the Relation " + relationName;
                    if (bind != null)
                    {
                        conflict.Device = bind.Joystick ?? string.Empty;
                        conflict.Input = (relation.ISAXIS ? bind.JAxis : bind.JButton) ?? string.Empty;
                        conflict.Modifiers = (bind.AllReformers ?? new List<string>()).Select(ModifierName).Where(m => m.Length > 0).ToList();
                    }
                    conflict.Kept = kept.Contains(ActionEntry(conflict));
                    if (conflict.Kept && !includeKept)
                        continue;
                    result.Add(conflict);
                }
            }
            return result;
        }

        // "Lpinky§<device>§JOY_BTN2§False" -> "Lpinky" (the modifier's name).
        private static string ModifierName(string reformer)
        {
            string text = (reformer ?? string.Empty).Trim();
            int cut = text.IndexOf('§');
            return (cut >= 0 ? text.Substring(0, cut) : text).Trim();
        }

        private static EngineConflictAction ActionOf(string game, string id, string plane, RelationItem node)
        {
            bool unknown;
            string title = EngineRelationDuplicates.TitleOf(game, id, plane, new[] { node }, out unknown);
            return new EngineConflictAction { Id = id ?? string.Empty, Title = title, Unknown = unknown };
        }

        // Keeps the input's function only in the relation keep: the aircraft is switched off in the other relations of
        // the conflict (each of their actions for it, one tick brings it back). Returns the changed actions.
        internal static int KeepOnly(EngineInputConflict conflict, string keep)
        {
            int changed = 0;
            foreach (EngineConflictSide side in conflict.Sides)
            {
                if (keep != null && string.Equals(side.Relation, keep, StringComparison.OrdinalIgnoreCase))
                    continue;
                changed += SwitchOff(side.Relation, conflict.Game, conflict.Plane, null);
            }
            return changed;
        }

        // Keeps only the action id of the relation for the aircraft: its other actions of the game are switched off.
        internal static int KeepAction(EngineActionConflict conflict, string id)
        {
            return SwitchOff(conflict.Relation, conflict.Game, conflict.Plane, id);
        }

        // Switches the aircraft off in the relation's actions of the game (except the action keepId).
        private static int SwitchOff(string relationName, string game, string plane, string keepId)
        {
            Relation relation;
            if (InternalDataManagement.AllRelations == null || !InternalDataManagement.AllRelations.TryGetValue(relationName ?? string.Empty, out relation) || relation == null)
                return 0;
            int changed = 0;
            foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
            {
                if (node == null || node.AIRCRAFT == null || !string.Equals(Game(node.Game), game, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (keepId != null && string.Equals(node.ID, keepId, StringComparison.OrdinalIgnoreCase))
                    continue;
                string key = node.AIRCRAFT.Keys.FirstOrDefault(k => string.Equals(k.Trim(), plane, StringComparison.OrdinalIgnoreCase));
                if (key == null || !node.AIRCRAFT[key])
                    continue;
                node.AIRCRAFT[key] = false;
                changed++;
            }
            return changed;
        }

        // "Keep both": the check stops naming these while exactly these relations (actions) have them.
        internal static int KeepBoth(IEnumerable<EngineInputConflict> inputs, IEnumerable<EngineActionConflict> actions)
        {
            if (InternalDataManagement.KeptDuplicates == null)
                InternalDataManagement.KeptDuplicates = new List<string>();
            int added = 0;
            foreach (string entry in inputs.Select(InputEntry).Concat(actions.Select(ActionEntry)))
            {
                if (InternalDataManagement.KeptDuplicates.Contains(entry, StringComparer.OrdinalIgnoreCase))
                    continue;
                InternalDataManagement.KeptDuplicates.Add(entry);
                added++;
            }
            return added;
        }

        // Takes the actions of the aircraft out of every relation that has them switched on (an unknown action the
        // game's list no longer has): "game|id|plane" keys. Returns the removed actions.
        internal static int RemoveActions(IEnumerable<string> keys)
        {
            int removed = 0;
            foreach (string key in keys)
            {
                string game;
                string id;
                string plane;
                if (!EngineRelationDuplicates.TrySplitKey(key, out game, out id, out plane))
                    continue;
                foreach (Relation relation in (InternalDataManagement.AllRelations ?? new Dictionary<string, Relation>()).Values.Where(r => r != null && r.NODES != null))
                {
                    foreach (RelationItem node in relation.NODES.ToList())
                    {
                        if (node == null || node.AIRCRAFT == null || !string.Equals(node.ID, id, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(Game(node.Game), game, StringComparison.OrdinalIgnoreCase))
                            continue;
                        string own = node.AIRCRAFT.Keys.FirstOrDefault(k => string.Equals(k.Trim(), plane, StringComparison.OrdinalIgnoreCase));
                        if (own == null)
                            continue;
                        node.DeleteAircraftFromActivity(own);
                        removed++;
                        if (node.AIRCRAFT.Count == 0)
                            relation.NODES.Remove(node);
                    }
                }
            }
            return removed;
        }

        internal static string ToText(EngineConflictsResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Inputs that do two things: " + result.InputConflicts.Count.ToString(CultureInfo.InvariantCulture));
            foreach (EngineInputConflict c in result.InputConflicts)
                sb.AppendLine("  - " + c.Game + " " + c.Plane + ": " + EngineRelationDuplicates.ShortDevice(c.Device) + " " + EngineRelationDuplicates.ShortInput(c.Input) +
                              (c.Modifiers.Count > 0 ? " + " + string.Join(" + ", c.Modifiers) : string.Empty) + " in " +
                              string.Join(" and ", c.Sides.Select(s => "'" + s.Relation + "' (" + string.Join(", ", s.Actions.Select(a => a.Title.Length > 0 ? a.Title : a.Id)) + ")")) +
                              (c.Kept ? " [kept]" : string.Empty));
            sb.AppendLine("Relations with two actions of one aircraft: " + result.ActionConflicts.Count.ToString(CultureInfo.InvariantCulture));
            foreach (EngineActionConflict c in result.ActionConflicts)
                sb.AppendLine("  - '" + c.Relation + "' " + c.Game + " " + c.Plane + ": " + string.Join(", ", c.Actions.Select(a => a.Title.Length > 0 ? a.Title : a.Id)) + (c.Kept ? " [kept]" : string.Empty));
            if (result.Changed > 0)
                sb.AppendLine("Changed: " + result.Changed.ToString(CultureInfo.InvariantCulture));
            foreach (string warning in result.Warnings)
                sb.AppendLine("Warning: " + warning);
            foreach (string error in result.Errors)
                sb.AppendLine("Error: " + error);
            return sb.ToString();
        }
    }
}
