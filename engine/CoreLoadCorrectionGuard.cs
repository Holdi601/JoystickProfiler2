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
