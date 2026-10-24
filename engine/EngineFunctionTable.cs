using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.Engine
{
    // The function table (DB\Functions\<game>.tsv: aircraft, slot, action, status) of a game that has none: derived from
    // the function map (EngineFunctionMap: every action of the game's own list, mapped by its readable title, category
    // and the map's synonyms and game terms). Per slot the game's best action of that function and direction, status
    // "derived" (counts less than a manual-checked row). So every game with a command list - FSX, Prepar3D, IL-2 1946,
    // Cliffs of Dover, Project Wingman, ACE COMBAT, Battlefield, Tiny Combat Arena, G-Rebels, Hell Let Loose: Vietnam
    // and any game added later - reaches search by function, suggestions, Auto Assign, the setup wizard and every AI
    // path without a hand-written table. A game's own table always wins (no derived row for a game that has one).
    internal static class EngineFunctionTable
    {
        public const string DerivedStatus = "derived";

        private static readonly object Sync = new object();
        private static Dictionary<string, List<EngineAutoGroup.Row>> cache;
        private static string cacheKey = string.Empty;
        [ThreadStatic] private static bool deriving;

        internal static void Reset()
        {
            lock (Sync)
            {
                cache = null;
                cacheKey = string.Empty;
            }
        }

        // Adds the derived rows (and a catalog entry for each such game) to a loaded table.
        internal static void AddDerived(string folder, Dictionary<string, EngineAutoGroup.Slot> slots, Dictionary<string, EngineAutoGroup.Aircraft> aircraft, List<EngineAutoGroup.Row> rows)
        {
            if (deriving)
                return;
            HashSet<string> own = new HashSet<string>(rows.Select(r => EngineRelationSuggest.GameKey(r.Game ?? string.Empty)), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<EngineAutoGroup.Row>> derived = Derive(folder, slots, own);
            foreach (KeyValuePair<string, List<EngineAutoGroup.Row>> game in derived)
            {
                if (own.Contains(game.Key) || game.Value.Count == 0)
                    continue;
                if (!aircraft.ContainsKey(game.Key))
                    aircraft[game.Key] = new EngineAutoGroup.Aircraft { Game = game.Key, Type = EngineGameWords.SpaceGames.Contains(game.Key) ? "spaceship" : "fixed_wing", Era = "modern" };
                rows.AddRange(game.Value);
            }
        }

        // Derived rows of one game (0 when the game has a table of its own or nothing maps).
        internal static int DerivedCount(string game)
        {
            string folder = EngineNlGroup.FunctionsFolder();
            EngineAutoGroupResult probe = new EngineAutoGroupResult();
            Dictionary<string, EngineAutoGroup.Slot> slots;
            Dictionary<string, EngineAutoGroup.Aircraft> aircraft;
            List<EngineAutoGroup.Row> rows;
            if (!EngineAutoGroup.TryLoadCatalog(folder, probe, out slots, out aircraft, out rows, true))
                return 0;
            HashSet<string> own = new HashSet<string>(rows.Select(r => EngineRelationSuggest.GameKey(r.Game ?? string.Empty)), StringComparer.OrdinalIgnoreCase);
            if (own.Contains(game))
                return 0;
            List<EngineAutoGroup.Row> list;
            return Derive(folder, slots, own).TryGetValue(game, out list) ? list.Count : 0;
        }

        private static Dictionary<string, List<EngineAutoGroup.Row>> Derive(string folder, Dictionary<string, EngineAutoGroup.Slot> slots, HashSet<string> own)
        {
            Dictionary<string, List<EngineAutoGroup.Row>> result = new Dictionary<string, List<EngineAutoGroup.Row>>(StringComparer.OrdinalIgnoreCase);
            EngineFunctionMap.Map map;
            deriving = true;
            try
            {
                map = EngineFunctionMap.Load(new List<string>());
            }
            catch (Exception)
            {
                return result;
            }
            finally
            {
                deriving = false;
            }
            string key = folder + "|" + string.Join(",", own.OrderBy(g => g, StringComparer.OrdinalIgnoreCase)) + "|" + slots.Count + "|" +
                         System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(map).ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + map.Entries.Count;
            lock (Sync)
            {
                if (cache != null && cacheKey == key)
                    return cache;
            }
            // slot -> (score, row): the best action per game and slot.
            Dictionary<string, Dictionary<string, KeyValuePair<double, EngineAutoGroup.Row>>> best = new Dictionary<string, Dictionary<string, KeyValuePair<double, EngineAutoGroup.Row>>>(StringComparer.OrdinalIgnoreCase);
            foreach (EngineFunctionMap.Entry e in map.Entries)
            {
                string game = e.Command.Game;
                if (string.IsNullOrEmpty(game) || game == "DCS" || own.Contains(game))
                    continue;
                foreach (EngineFunctionMap.Assignment a in e.Functions)
                {
                    if (a.Weight < 0.9 || EngineIdMeanings.CatalogAgainstTitle(e, a))
                        continue;
                    foreach (EngineFunctionMap.SlotLink link in a.Function.Slots)
                    {
                        EngineAutoGroup.Slot slot;
                        if (!slots.TryGetValue(link.Slot, out slot) || slot.Axis != e.Command.IsAxis)
                            continue;
                        // A slot of one direction takes only an action of that direction (gear up is not gear down).
                        if (link.Dir.Length > 0 && !string.Equals(a.Dir, link.Dir, StringComparison.OrdinalIgnoreCase))
                            continue;
                        double score = a.Weight * link.Weight + (a.InTitle ? 0.1 : 0) - EngineFunctionMap.PrimaryRank(e, a.Function) * 0.01;
                        Dictionary<string, KeyValuePair<double, EngineAutoGroup.Row>> perSlot;
                        if (!best.TryGetValue(game, out perSlot))
                            best[game] = perSlot = new Dictionary<string, KeyValuePair<double, EngineAutoGroup.Row>>(StringComparer.OrdinalIgnoreCase);
                        KeyValuePair<double, EngineAutoGroup.Row> known;
                        if (perSlot.TryGetValue(link.Slot, out known) && known.Key >= score)
                            continue;
                        perSlot[link.Slot] = new KeyValuePair<double, EngineAutoGroup.Row>(score, new EngineAutoGroup.Row { Game = game, Plane = game, Slot = link.Slot, Action = e.Command.Id, Status = DerivedStatus });
                    }
                }
            }
            foreach (KeyValuePair<string, Dictionary<string, KeyValuePair<double, EngineAutoGroup.Row>>> game in best)
                result[game.Key] = game.Value.Values.Select(v => v.Value).OrderBy(r => r.Slot, StringComparer.Ordinal).ToList();
            lock (Sync)
            {
                cache = result;
                cacheKey = key;
            }
            return result;
        }
    }
}
