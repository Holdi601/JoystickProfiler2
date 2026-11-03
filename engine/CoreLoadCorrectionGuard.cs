using System;
using System.Collections.Generic;
using System.Linq;
using JoyPro.Engine;

namespace JoyPro
{
    // The load correction's "add the DB's other aircraft of a node's id" step (--auto-add-db-items) switches an aircraft
    // on in a node only when that is safe (docs/relation-builder.md "Same id, different meaning"):
    //   - the same action of the same aircraft is not switched on in another relation already: one action on two
    //     inputs is the same DCS command bound twice (a tester's DCS had his AH-64 force trim on two devices after
    //     every module the profile did not mention yet had been switched on everywhere its id occurs);
    //   - in DCS the aircraft's title of the id means what the node's switched-on aircraft mean (same or near, or the
    //     function map reads both as the same function), as every builder that adds many aircraft at once does.
    // Otherwise the aircraft goes into the node switched off: "left out", one tick switches it on.
    public static partial class InternalDataManagement
    {
        // During one correction pass: "game|id|plane" -> the relations that have it switched on.
        private static Dictionary<string, List<string>> correctionActiveKeys;

        private static string CorrectionActiveKey(string game, string id, string plane)
        {
            return NormalizeGameNameForCorrection(game) + "|" + (id ?? string.Empty).Trim() + "|" + (plane ?? string.Empty).Trim();
        }

        private static void BeginCorrectionActiveIndex()
        {
            Dictionary<string, List<string>> keys = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (Relation relation in (AllRelations ?? new Dictionary<string, Relation>()).Values)
            {
                if (relation == null || relation.NODES == null)
                    continue;
                foreach (RelationItem node in relation.NODES)
                {
                    if (node == null || node.AIRCRAFT == null || string.IsNullOrWhiteSpace(node.ID))
                        continue;
                    foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT)
                    {
                        if (plane.Value)
                            NoteCorrectionActive(keys, node.Game, node.ID, plane.Key, relation.NAME);
                    }
                }
            }
            correctionActiveKeys = keys;
        }

        private static void EndCorrectionActiveIndex()
        {
            correctionActiveKeys = null;
        }

        private static void NoteCorrectionActive(Dictionary<string, List<string>> keys, string game, string id, string plane, string relationName)
        {
            if (keys == null || string.IsNullOrWhiteSpace(plane))
                return;
            string key = CorrectionActiveKey(game, id, plane);
            List<string> owners;
            if (!keys.TryGetValue(key, out owners))
                keys[key] = owners = new List<string>();
            string name = relationName ?? string.Empty;
            if (!owners.Contains(name, StringComparer.OrdinalIgnoreCase))
                owners.Add(name);
        }

        // The other relation that has this action of this aircraft switched on (null: none).
        private static string CorrectionActiveElsewhere(string game, string id, string plane, Relation relation)
        {
            if (correctionActiveKeys == null)
                return null;
            List<string> owners;
            if (!correctionActiveKeys.TryGetValue(CorrectionActiveKey(game, id, plane), out owners))
                return null;
            string own = relation == null ? string.Empty : relation.NAME ?? string.Empty;
            return owners.FirstOrDefault(o => !string.Equals(o, own, StringComparison.OrdinalIgnoreCase));
        }

        // Whether the correction may switch this DB aircraft on in the node (the node's own and the relation's checks
        // were done by the caller). reason: why not, for the correction report.
        private static bool CorrectionMayActivate(Relation relation, RelationItem item, string game, string plane, out string reason)
        {
            reason = string.Empty;
            string elsewhere = CorrectionActiveElsewhere(game, item == null ? string.Empty : item.ID, plane, relation);
            if (elsewhere != null)
            {
                reason = "the action is already on in relation '" + elsewhere + "' for this aircraft";
                return false;
            }
            if (item == null || !string.Equals(NormalizeGameNameForCorrection(game), "DCS", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!DcsMeaningFits(relation, item, plane))
            {
                reason = "the id means something else in this aircraft than in the node's aircraft";
                return false;
            }
            return true;
        }

        // DCS: the aircraft's title of the id means what the node's switched-on aircraft mean.
        private static bool DcsMeaningFits(Relation relation, RelationItem item, string plane)
        {
            try
            {
                List<EngineRelationSuggest.Command> references = (item.AIRCRAFT ?? new Dictionary<string, bool>())
                    .Where(p => p.Value && !string.Equals(p.Key, plane, StringComparison.OrdinalIgnoreCase))
                    .Select(p => EngineIdMeanings.Find("DCS", item.ID, p.Key))
                    .Where(c => c != null)
                    .ToList();
                EngineRelationSuggest.Command candidate = EngineIdMeanings.Find("DCS", item.ID, plane);
                // Nothing to compare with (no switched-on aircraft with the id in its list, or no title of the aircraft):
                // left out, a tick puts it in.
                if (references.Count == 0 || candidate == null)
                    return false;
                List<EngineIdMeanings.Cluster> clusters = EngineIdMeanings.Clusters(references);
                EngineIdMeanings.Cluster main = EngineIdMeanings.MainCluster(clusters, relation == null ? string.Empty : relation.NAME);
                if (main == null || main.Head == null || main.Head.Sample == null)
                    return false;
                string note;
                EngineIdMeanings.Kind kind = EngineIdMeanings.Compare(main.Head.Sample, candidate, out note);
                if (kind != EngineIdMeanings.Kind.Different)
                    return true;
                return main.Head.Planes.Count > 0 && EngineIdMeanings.SameFunction("DCS", item.ID, main.Head.Planes[0], plane);
            }
            catch (Exception ex)
            {
                JoyPro.Logging.JoyProLog.Debug("Corrections", "Meaning check of " + plane + " " + item.ID + " failed: " + ex.Message);
                return false;
            }
        }

        // The correction switched an aircraft on: later nodes of other relations see it.
        private static void NoteCorrectionActivated(string game, string id, string plane, Relation relation)
        {
            NoteCorrectionActive(correctionActiveKeys, game, id, plane, relation == null ? string.Empty : relation.NAME);
        }

        private static void RecordAutoAddAdded(Relation relation, RelationItem item, string game, string plane)
        {
            RecordLoadCorrectionEntry(new LoadCorrectionReportEntry
            {
                Kind = "relation-item-auto-add",
                Action = "added",
                RelationName = relation == null ? string.Empty : relation.NAME ?? string.Empty,
                Game = game ?? string.Empty,
                OldId = item == null ? string.Empty : item.ID ?? string.Empty,
                NewId = item == null ? string.Empty : item.ID ?? string.Empty,
                NewPlane = plane ?? string.Empty,
                Confidence = "high",
                Score = 100,
                Reason = "Aircraft new in the command lists since the profile was last corrected: switched on"
            });
        }

        // ---- Which aircraft are new in the command lists (2.6.2) ----
        //
        // "Auto Add DB Items" is for a module that came into the command lists after the profile was corrected or saved
        // (JoyPro 1: "Auto Add on new DB Items"; its nodes held every aircraft of the id, so an aircraft missing in a node
        // was one the lists had not had). JoyPro 2's imports put only the bound aircraft into a node, so "not in the
        // node" no longer means "new": up to 2.6.1 the pass switched on every aircraft of the id the user had never
        // bound (a tester's "HELO POWER BOTH" went from 4 aircraft to 21 after Refresh DB). Now the profile keeps the
        // aircraft of the lists next to its DB fingerprint (Pr0file.DatabasePlanes) and only an aircraft that is not in
        // that set counts as new; without a set (every older profile) the set is recorded and nothing is switched on.
        // An id that a DCS patch or a mod adds to an aircraft that was there already is not switched on either: that
        // aircraft was the user's to set up, and a new command of it is not the user's choice.

        // Pr0file.DatabasePlanes of the loaded profile ("game<TAB>plane"); null = the profile has none.
        internal static HashSet<string> LoadedProfileDatabasePlanes;
        // The games the loaded set has lists of.
        private static HashSet<string> loadedProfileDatabasePlaneGames;
        // The aircraft of the current lists, once read in this run (the correction pass or a save).
        private static List<string> currentDatabasePlanes;

        private static void SetLoadedDatabasePlanes(List<string> stored)
        {
            currentDatabasePlanes = null;
            if (stored == null)
            {
                LoadedProfileDatabasePlanes = null;
                loadedProfileDatabasePlaneGames = null;
                return;
            }
            LoadedProfileDatabasePlanes = new HashSet<string>(stored.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
            loadedProfileDatabasePlaneGames = new HashSet<string>(LoadedProfileDatabasePlanes.Select(s => s.Split('\t')[0]), StringComparer.OrdinalIgnoreCase);
        }

        private static string DatabasePlaneKey(string game, string plane)
        {
            return NormalizeGameNameForCorrection(game) + "\t" + (plane ?? string.Empty).Trim();
        }

        // The aircraft of the correction's command lists (the same the pass reads: DCS one list per aircraft, IL-2 and
        // Star Citizen one per game).
        private static List<string> DatabasePlanesOf(DbCorrectionIndex index)
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (index != null)
            {
                foreach (DCSInput input in index.AllDcsInputs ?? new List<DCSInput>())
                {
                    if (input != null && !string.IsNullOrWhiteSpace(input.Plane))
                        keys.Add(DatabasePlaneKey("DCS", input.Plane));
                }
                foreach (KeyValuePair<string, List<OtherGameInput>> game in index.AllOtherInputsByGame ?? new Dictionary<string, List<OtherGameInput>>())
                {
                    foreach (OtherGameInput input in game.Value ?? new List<OtherGameInput>())
                    {
                        if (input != null)
                            keys.Add(DatabasePlaneKey(game.Key, string.IsNullOrWhiteSpace(input.Plane) ? game.Key : input.Plane));
                    }
                }
            }
            return keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Without the correction's index (the profile matched the lists): the same set from the files' names, without
        // reading the lists (DCS: one list file per aircraft).
        private static List<string> DatabasePlanesFromFiles()
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string dcs = GameDataPaths.DbFolder("DCS");
                if (System.IO.Directory.Exists(dcs))
                {
                    foreach (string file in System.IO.Directory.GetFiles(dcs, "*.html", System.IO.SearchOption.TopDirectoryOnly))
                        keys.Add(DatabasePlaneKey("DCS", System.IO.Path.GetFileNameWithoutExtension(file)));
                }
                if (System.IO.File.Exists(System.IO.Path.Combine(GameDataPaths.DbFolder("IL2"), "IL2.actions")))
                    keys.Add(DatabasePlaneKey("IL2Game", "IL2Game"));
                string sc = GameDataPaths.DbFolder("StarCitizen");
                if (System.IO.File.Exists(System.IO.Path.Combine(sc, "AllBinds.xml")) || System.IO.File.Exists(System.IO.Path.Combine(sc, "actionmaps.xml")))
                    keys.Add(DatabasePlaneKey("StarCitizen", "StarCitizen"));
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                JoyPro.Logging.JoyProLog.Debug("Corrections", "No aircraft of the command lists: " + ex.Message);
            }
            return keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // What a save stores in Pr0file.DatabasePlanes: the current lists' aircraft when the profile matches the current
        // lists (the pass saw them, or the fingerprint matched), else the set the profile had (its fingerprint stays too).
        private static List<string> DatabasePlanesToSave()
        {
            if (!ProfileMatchesCurrentDatabase)
                return LoadedProfileDatabasePlanes == null ? null : LoadedProfileDatabasePlanes.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            if (currentDatabasePlanes == null)
            {
                // The fingerprint matched: the stored set is the current one; a profile without one gets it once.
                currentDatabasePlanes = LoadedProfileDatabasePlanes != null
                    ? LoadedProfileDatabasePlanes.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList()
                    : DatabasePlanesFromFiles();
            }
            return currentDatabasePlanes;
        }

        // Input layers that are no aircraft ("Default", the cameras, the radio menu): never switched on by the pass.
        internal static bool IsPseudoPlane(string game, string plane)
        {
            return string.Equals(NormalizeGameNameForCorrection(game), "DCS", StringComparison.OrdinalIgnoreCase) && EngineDcsExport.IsNonAircraftLayer(plane);
        }

        // An aircraft of the lists that the profile's set did not have: a module added since the last correction.
        private static bool IsPlaneNewInDb(string game, string plane)
        {
            if (LoadedProfileDatabasePlanes == null || loadedProfileDatabasePlaneGames == null || string.IsNullOrWhiteSpace(plane))
                return false;
            string g = NormalizeGameNameForCorrection(game);
            if (!loadedProfileDatabasePlaneGames.Contains(g))
                return false;
            if (LoadedProfileDatabasePlanes.Contains(DatabasePlaneKey(g, plane)))
                return false;
            return !IsPseudoPlane(g, plane);
        }

        // The pass's gate entry: which aircraft are new, or that the set was recorded this time.
        private static void RecordDatabasePlanesEntry(List<string> current)
        {
            List<string> added = LoadedProfileDatabasePlanes == null
                ? new List<string>()
                : current.Where(k => !LoadedProfileDatabasePlanes.Contains(k) && loadedProfileDatabasePlaneGames.Contains(k.Split('\t')[0])).ToList();
            RecordLoadCorrectionEntry(new LoadCorrectionReportEntry
            {
                Kind = "db-plane-set",
                Action = LoadedProfileDatabasePlanes == null ? "recorded" : "compared",
                Confidence = "high",
                Score = 100,
                Reason = LoadedProfileDatabasePlanes == null
                    ? "The profile had no aircraft set of the command lists: recorded now, no aircraft switched on this time."
                    : added.Count == 0
                        ? "No aircraft is new in the command lists: no aircraft switched on."
                        : added.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " aircraft new in the command lists may be switched on.",
                Details = "aircraft=" + current.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "; new=" + string.Join(", ", added.Select(k => k.Replace('\t', ':')))
            });
        }

        // ---- What the pass changed (its own history step, Program.SaveLoadCorrections) ----

        // "relation<TAB>game<TAB>id<TAB>plane" -> on, of every node.
        private static Dictionary<string, bool> CorrectionSnapshot()
        {
            Dictionary<string, bool> map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> entry in AllRelations ?? new Dictionary<string, Relation>())
            {
                Relation relation = entry.Value;
                if (relation == null || relation.NODES == null)
                    continue;
                foreach (RelationItem node in relation.NODES)
                {
                    if (node == null || node.AIRCRAFT == null)
                        continue;
                    foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT)
                        map[entry.Key + "\t" + NormalizeGameNameForCorrection(node.Game) + "\t" + node.ID + "\t" + plane.Key] = plane.Value;
                }
            }
            return map;
        }

        // What the DB part of the last load correction changed in the relations: "+key" switched on, "-key" switched off
        // or taken out, "~key" put in switched off (key as in CorrectionSnapshot). Empty: nothing.
        public static List<string> LoadCorrectionChanges = new List<string>();

        private static List<string> DescribeCorrectionChanges(Dictionary<string, bool> before, Dictionary<string, bool> after)
        {
            List<string> changes = new List<string>();
            foreach (KeyValuePair<string, bool> now in after)
            {
                bool was;
                bool had = before.TryGetValue(now.Key, out was);
                if (had && was == now.Value)
                    continue;
                changes.Add((now.Value ? "+" : had ? "-" : "~") + now.Key);
            }
            foreach (KeyValuePair<string, bool> old in before)
            {
                if (!after.ContainsKey(old.Key) && old.Value)
                    changes.Add("-" + old.Key);
            }
            changes.Sort(StringComparer.OrdinalIgnoreCase);
            return changes;
        }

        // "Updated to the new command lists: 3 aircraft in 2 relations" (empty: nothing changed).
        public static string LoadCorrectionLabel()
        {
            List<string> changes = LoadCorrectionChanges ?? new List<string>();
            if (changes.Count == 0)
                return string.Empty;
            int relations = changes.Select(c => c.Substring(1).Split('\t')[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return "Updated to the new command lists: " + changes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " aircraft in " +
                   relations.ToString(System.Globalization.CultureInfo.InvariantCulture) + (relations == 1 ? " relation" : " relations");
        }

        // The changes in words for the history note and the log ("'Pitch': F-16C_50 switched on; ...").
        public static string LoadCorrectionNote(int max = 12)
        {
            List<string> changes = LoadCorrectionChanges ?? new List<string>();
            List<string> parts = new List<string>();
            foreach (IGrouping<string, string> relation in changes.GroupBy(c => c.Substring(1).Split('\t')[0], StringComparer.OrdinalIgnoreCase).Take(max))
            {
                List<string> words = new List<string>();
                foreach (string change in relation)
                {
                    string[] key = change.Substring(1).Split('\t');
                    string plane = key.Length > 3 ? key[3] : string.Empty;
                    words.Add(plane + (change[0] == '+' ? " switched on" : change[0] == '~' ? " added switched off" : " taken out"));
                }
                parts.Add("'" + relation.Key + "': " + string.Join(", ", words));
            }
            int more = changes.Select(c => c.Substring(1).Split('\t')[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count() - parts.Count;
            return LoadCorrectionLabel() + (parts.Count > 0 ? " (" + string.Join("; ", parts) + (more > 0 ? "; " + more.ToString(System.Globalization.CultureInfo.InvariantCulture) + " more relations" : string.Empty) + ")" : string.Empty);
        }

        private static void RecordAutoAddLeftOut(Relation relation, RelationItem item, string game, string plane, string reason)
        {
            RecordLoadCorrectionEntry(new LoadCorrectionReportEntry
            {
                Kind = "relation-item-auto-add",
                Action = "left-out",
                RelationName = relation == null ? string.Empty : relation.NAME ?? string.Empty,
                Game = game ?? string.Empty,
                OldId = item == null ? string.Empty : item.ID ?? string.Empty,
                NewId = item == null ? string.Empty : item.ID ?? string.Empty,
                NewPlane = plane ?? string.Empty,
                Confidence = "high",
                Score = 100,
                Reason = "Aircraft added switched off: " + reason
            });
        }
    }
}
