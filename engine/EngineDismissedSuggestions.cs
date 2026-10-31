using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JoyPro.Engine
{
    // Suggestions the user dismissed for a relation (Pr0file.DismissedSuggestions, docs/relation-builder.md "Dismissed
    // suggestions"): "relation<TAB>game<TAB>id<TAB>plane" (plane = the game for a game without aircraft). Relations
    // suggest does not propose them again, Auto Group and the grouping in words keep them out of that relation, and
    // "Restore" (relations suggest apply --remove) takes them off the list.
    //
    // Before 2.5.0 a dismissed suggestion was an action of the relation with no aircraft ticked. Such actions - also the
    // ones the user unticked completely - are taken out of their relation when the profile is opened (engine profile
    // load) and their aircraft go onto this list, so the suggestion still does not come back. Unticking every aircraft
    // for a moment changes nothing until the profile is opened again; an action with at least one aircraft ticked, and
    // every relation (also one without a bind, the user's markers), always stays.
    internal static class EngineDismissedSuggestions
    {
        internal sealed class Entry
        {
            public string Relation = string.Empty;
            public string Game = string.Empty;
            public string Id = string.Empty;
            public string Plane = string.Empty;
        }

        private static string GameOf(string game)
        {
            return string.IsNullOrWhiteSpace(game) ? "DCS" : game.Trim();
        }

        internal static string Format(string relation, string game, string id, string plane)
        {
            return (relation ?? string.Empty) + "\t" + GameOf(game) + "\t" + (id ?? string.Empty) + "\t" + (plane ?? string.Empty).Trim();
        }

        internal static Entry Parse(string line)
        {
            string[] parts = (line ?? string.Empty).Split('\t');
            if (parts.Length < 4 || parts[0].Length == 0 || parts[2].Length == 0)
                return null;
            return new Entry { Relation = parts[0], Game = parts[1], Id = parts[2], Plane = parts[3] };
        }

        internal static List<Entry> All()
        {
            return (InternalDataManagement.DismissedSuggestions ?? new List<string>()).Select(Parse).Where(e => e != null).ToList();
        }

        internal static List<Entry> Of(string relation)
        {
            return All().Where(e => string.Equals(e.Relation, relation ?? string.Empty, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        internal static void Add(string relation, string game, string id, string plane)
        {
            if (InternalDataManagement.DismissedSuggestions == null)
                InternalDataManagement.DismissedSuggestions = new List<string>();
            string entry = Format(relation, game, id, plane);
            if (!InternalDataManagement.DismissedSuggestions.Contains(entry, StringComparer.OrdinalIgnoreCase))
                InternalDataManagement.DismissedSuggestions.Add(entry);
        }

        // Takes these aircraft of the action off the relation's list (Restore). Returns how many were on it.
        internal static int Remove(string relation, string game, string id, IEnumerable<string> planes)
        {
            if (InternalDataManagement.DismissedSuggestions == null)
                return 0;
            HashSet<string> wanted = new HashSet<string>((planes ?? new string[0]).Select(p => (p ?? string.Empty).Trim()), StringComparer.OrdinalIgnoreCase);
            return InternalDataManagement.DismissedSuggestions.RemoveAll(line =>
            {
                Entry e = Parse(line);
                return SameAction(e, relation, game, id) && wanted.Contains(e.Plane);
            });
        }

        private static bool SameAction(Entry e, string relation, string game, string id)
        {
            return e != null && string.Equals(e.Relation, relation, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(EngineRelationSuggest.GameKey(e.Game), EngineRelationSuggest.GameKey(GameOf(game)), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(EngineRelationSuggest.NodeId(EngineRelationSuggest.GameKey(e.Game), e.Id), EngineRelationSuggest.NodeId(EngineRelationSuggest.GameKey(GameOf(game)), id), StringComparison.OrdinalIgnoreCase);
        }

        // A renamed relation keeps its dismissed suggestions.
        internal static void Rename(string oldName, string newName)
        {
            List<string> list = InternalDataManagement.DismissedSuggestions;
            if (list == null || list.Count == 0 || string.Equals(oldName, newName, StringComparison.Ordinal))
                return;
            for (int i = 0; i < list.Count; i++)
            {
                Entry e = Parse(list[i]);
                if (e != null && string.Equals(e.Relation, oldName, StringComparison.OrdinalIgnoreCase))
                    list[i] = Format(newName, e.Game, e.Id, e.Plane);
            }
        }

        // A relation's action with no aircraft ticked: aircraft listed and all switched off, or (DCS) none listed at all.
        // A game without aircraft lists none for "on" (EngineRelationSuggest.Coverage): that one stays.
        internal static bool HasNoAircraftTicked(RelationItem node)
        {
            if (node == null || string.IsNullOrWhiteSpace(node.ID))
                return false;
            Dictionary<string, bool> aircraft = node.AIRCRAFT ?? new Dictionary<string, bool>();
            if (aircraft.Count == 0)
                return string.Equals(EngineRelationSuggest.GameKey(GameOf(node.Game)), "DCS", StringComparison.OrdinalIgnoreCase);
            return !aircraft.Values.Any(v => v);
        }

        // When the profile is opened: every action with no aircraft ticked leaves its relation; its aircraft go onto the
        // list of dismissed suggestions of that relation. Returns one line per relation ("'Trim': 2 actions").
        internal static List<string> RemoveUntickedActions()
        {
            List<string> report = new List<string>();
            foreach (Relation relation in (InternalDataManagement.AllRelations ?? new Dictionary<string, Relation>()).Values.Where(r => r != null && r.NODES != null))
            {
                int removed = 0;
                foreach (RelationItem node in relation.NODES.ToList())
                {
                    if (!HasNoAircraftTicked(node))
                        continue;
                    string game = GameOf(node.Game);
                    List<string> planes = (node.AIRCRAFT ?? new Dictionary<string, bool>()).Keys.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                    if (planes.Count == 0 && !string.Equals(EngineRelationSuggest.GameKey(game), "DCS", StringComparison.OrdinalIgnoreCase))
                        planes.Add(game);
                    foreach (string plane in planes)
                        Add(relation.NAME, game, node.ID, plane);
                    relation.NODES.Remove(node);
                    removed++;
                }
                if (removed > 0)
                    report.Add("'" + relation.NAME + "': " + removed.ToString(CultureInfo.InvariantCulture) + (removed == 1 ? " action" : " actions"));
            }
            return report;
        }

        // "game|id|plane" keys (the way EngineAutoGroup / EngineNlGroup name a node's aircraft) with their relations.
        internal static IEnumerable<KeyValuePair<string, Entry>> ForKeys(Func<string, string, string, string> nodeKey)
        {
            foreach (Entry e in All())
                yield return new KeyValuePair<string, Entry>(nodeKey(e.Game, e.Id, e.Plane), e);
        }
    }
}
