using System;
using System.Collections.Generic;
using System.Linq;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // Games JoyPro once handled as one game and now as one game per version (each with its own install, config files,
    // catalog and export target): "MSFS" -> MSFS2024 + MSFS2020, "XPlane" -> XPlane12 + XPlane11. The old id stays an
    // alias of the first (newest) version for the CLI (engine msfs / engine xplane, --node MSFS:...), and data saved
    // with it is migrated when it is loaded:
    //   - relation nodes: one node per version (same action id and node state; a version whose catalog does not have
    //     the action gets no copy, the first version always keeps one), see MigrateRelation;
    //   - tuning scopes: game:<old> -> family:<family> (shared by the versions), plane:<old>:<p> -> plane:<new>:<p>;
    //   - game settings (ffb set --game): <device>|game:<old> -> every version (only the settings the version knows);
    //   - Settings > Games (installs, config locations, "I do not have it"): see GameInstalls.
    // Until then (e.g. a node added with the old id in the same run) a node with the old id belongs to every version.
    internal static class LegacyGameIds
    {
        private static readonly Dictionary<string, string[]> Splits = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "MSFS", new[] { "MSFS2024", "MSFS2020" } },
            { "XPlane", new[] { "XPlane12", "XPlane11" } }
        };

        private static readonly Dictionary<string, GameCatalog> Catalogs = new Dictionary<string, GameCatalog>(StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<string> All
        {
            get { return Splits.Keys; }
        }

        public static bool IsLegacy(string gameId)
        {
            return !string.IsNullOrWhiteSpace(gameId) && Splits.ContainsKey(gameId.Trim());
        }

        // The versions an old id stands for (newest first); any other id as it is.
        public static IReadOnlyList<string> Expand(string gameId)
        {
            string[] ids;
            if (!string.IsNullOrWhiteSpace(gameId) && Splits.TryGetValue(gameId.Trim(), out ids))
                return ids;
            return new[] { (gameId ?? string.Empty).Trim() };
        }

        // The old id of a version (empty when the game was never split).
        public static string LegacyOf(string gameId)
        {
            foreach (KeyValuePair<string, string[]> split in Splits)
            {
                if (split.Value.Contains((gameId ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase))
                    return split.Key;
            }
            return string.Empty;
        }

        // Replaces each node of an old id by one node per version. Returns true when something changed.
        public static bool MigrateRelation(Relation relation)
        {
            if (relation == null || relation.NODES == null)
                return false;
            bool changed = false;
            for (int i = relation.NODES.Count - 1; i >= 0; i--)
            {
                RelationItem node = relation.NODES[i];
                if (node == null || !IsLegacy(node.Game))
                    continue;
                string legacy = node.Game.Trim();
                List<RelationItem> copies = new List<RelationItem>();
                IReadOnlyList<string> versions = Expand(legacy);
                for (int v = 0; v < versions.Count; v++)
                {
                    string version = versions[v];
                    string id = (node.ID ?? string.Empty).Trim();
                    if (v > 0 && !CatalogAllows(version, id))
                        continue;
                    Dictionary<string, bool> aircraft = RenameKey(node.AIRCRAFT, legacy, version);
                    RelationItem existing = relation.NODES.FirstOrDefault(n => n != null && n != node && string.Equals(n.Game, version, StringComparison.OrdinalIgnoreCase) &&
                                                                              string.Equals((n.ID ?? string.Empty).Trim(), id, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        // The version already has the action: it gets the old node's active aircraft too.
                        foreach (KeyValuePair<string, bool> plane in aircraft.Where(p => p.Value))
                            existing.SetAircraftActivity(plane.Key, true);
                        continue;
                    }
                    RelationItem copy = node.Copy();
                    copy.Game = version;
                    copy.AIRCRAFT = aircraft;
                    copies.Add(copy);
                }
                relation.NODES.RemoveAt(i);
                relation.NODES.InsertRange(i, copies);
                changed = true;
            }
            if (changed)
                relation.RecalculateElementCount();
            return changed;
        }

        // A version gets a copy of an old node only when its catalog has the action (X-Plane 11 lacks the X-Plane 12
        // axes and commands). Without a catalog file every action counts.
        private static bool CatalogAllows(string gameId, string actionId)
        {
            GameCatalog catalog;
            lock (Catalogs)
            {
                if (!Catalogs.TryGetValue(gameId, out catalog))
                {
                    IGameIntegration game;
                    catalog = GameRegistry.TryResolve(gameId, out game) ? GameCatalogStore.Load(game, null) : new GameCatalog();
                    Catalogs[gameId] = catalog;
                }
            }
            return catalog.Actions.Count == 0 || catalog.Contains(actionId);
        }

        private static Dictionary<string, bool> RenameKey(Dictionary<string, bool> aircraft, string from, string to)
        {
            Dictionary<string, bool> renamed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, bool> entry in aircraft ?? new Dictionary<string, bool>())
            {
                string key = string.Equals(entry.Key, from, StringComparison.OrdinalIgnoreCase) ? to : entry.Key;
                bool existing;
                renamed[key] = renamed.TryGetValue(key, out existing) ? existing || entry.Value : entry.Value;
            }
            return renamed;
        }

        // Tuning scopes of a bind: game:<old> -> family:<family>, plane:<old>:<p> -> plane:<version>:<p>.
        public static bool MigrateTuningScopes(Dictionary<string, string> overrides)
        {
            if (overrides == null || overrides.Count == 0)
                return false;
            bool changed = false;
            foreach (string scope in overrides.Keys.ToList())
            {
                string[] parts = scope.Split(':');
                if (parts.Length < 2 || !IsLegacy(parts[1]))
                    continue;
                string text = overrides[scope];
                if (parts[0] == "game" && parts.Length == 2)
                {
                    string family = "family:" + BindTuning.FamilyOf(Expand(parts[1])[0]);
                    if (!overrides.ContainsKey(family))
                        overrides[family] = text;
                }
                else if (parts[0] == "plane" && parts.Length >= 3)
                {
                    string plane = string.Join(":", parts.Skip(2));
                    foreach (string version in Expand(parts[1]))
                    {
                        string target = BindTuning.PlaneScope(version, plane);
                        if (!overrides.ContainsKey(target))
                            overrides[target] = text;
                    }
                }
                else
                {
                    continue;
                }
                overrides.Remove(scope);
                changed = true;
            }
            return changed;
        }

        // Game settings (FfbSettings store, "<device>|<scope>"): an old game's scope goes to every version, with the
        // settings that version's model knows.
        public static bool MigrateGameSettings(Dictionary<string, string> store)
        {
            if (store == null || store.Count == 0)
                return false;
            bool changed = false;
            foreach (string key in store.Keys.ToList())
            {
                int bar = key.IndexOf('|');
                if (bar < 0)
                    continue;
                string device = key.Substring(0, bar);
                string[] parts = key.Substring(bar + 1).Split(':');
                if (parts.Length < 2 || (parts[0] != "game" && parts[0] != "plane") || !IsLegacy(parts[1]))
                    continue;
                Dictionary<string, double> values = FfbSettings.Parse(store[key]);
                foreach (string version in Expand(parts[1]))
                {
                    string scope = parts[0] == "game" ? BindTuning.GameScope(version) : BindTuning.PlaneScope(version, string.Join(":", parts.Skip(2)));
                    string target = FfbSettings.Key(device, scope);
                    if (store.ContainsKey(target))
                        continue;
                    FfbModel model = FfbSettings.ForGame(version);
                    Dictionary<string, double> known = values
                        .Where(v => model == null || FfbSettings.Parameter(version, v.Key) != null)
                        .ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
                    if (known.Count > 0)
                        store[target] = FfbSettings.Format(known);
                }
                store.Remove(key);
                changed = true;
            }
            return changed;
        }
    }
}
