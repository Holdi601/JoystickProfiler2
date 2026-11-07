using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    public sealed class EngineJoinInputsGroup
    {
        // The relation that keeps the others' inputs as its extra binds.
        public string Relation { get; set; } = string.Empty;
        // The relations that go into it (removed when applied).
        public List<string> Joined { get; set; } = new List<string>();
        // Every input of the group, "device::input" (the kept relation's first).
        public List<string> Inputs { get; set; } = new List<string>();
        public bool IsAxis { get; set; }
        public int Actions { get; set; }
    }

    public sealed class EngineJoinInputsResult
    {
        public bool DryRun { get; set; }
        public bool Applied { get; set; }
        public int RelationsRemoved { get; set; }
        public int InputsAdded { get; set; }
        public List<EngineJoinInputsGroup> Groups { get; set; } = new List<EngineJoinInputsGroup>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    // Experimental multi-bind (docs/multi-bind.md "Import"): relations that do the same on different inputs (the same
    // actions with the same aircraft switched on, one input each) become one relation with several inputs. An import
    // makes a relation per input when a game file has an action on two devices; with the setting on it names these and
    // offers this (engine relations join-inputs --apply), the setting off imports exactly as before.
    internal static class EngineJoinInputs
    {
        internal sealed class Group
        {
            public Relation Target;
            public List<Relation> Others = new List<Relation>();
            public EngineJoinInputsGroup Info;
        }

        // What the relation does: every active (game, action, aircraft), sorted. Empty: nothing on.
        private static string Function(Relation relation)
        {
            List<string> parts = new List<string>();
            foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
            {
                if (node == null || string.IsNullOrWhiteSpace(node.ID))
                    continue;
                string game = string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game.Trim();
                List<string> planes = (node.AIRCRAFT ?? new Dictionary<string, bool>()).Where(p => p.Value && !string.IsNullOrWhiteSpace(p.Key)).Select(p => p.Key.Trim().ToLowerInvariant()).OrderBy(p => p, StringComparer.Ordinal).ToList();
                if (planes.Count == 0)
                    continue;
                parts.Add(game.ToLowerInvariant() + "|" + node.ID.Trim() + "|" + string.Join(",", planes));
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join("\n", parts);
        }

        private static bool HasGame(Relation relation, string game)
        {
            if (string.IsNullOrWhiteSpace(game))
                return true;
            string resolved = GameInstalls.Resolve(game);
            return (relation.NODES ?? new List<RelationItem>()).Any(n => n != null &&
                (string.Equals((n.Game ?? "DCS").Trim(), game.Trim(), StringComparison.OrdinalIgnoreCase) ||
                 (resolved.Length > 0 && string.Equals((n.Game ?? "DCS").Trim(), resolved, StringComparison.OrdinalIgnoreCase))));
        }

        // The relation of a group that keeps the inputs: one without a copy suffix ("__2", " (2)"), then the shortest
        // name, then by name.
        private static int CopyRank(string name)
        {
            string trimmed = (name ?? string.Empty).Trim();
            if (EngineDcsImport.IsCopyName(trimmed))
                return 1;
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"\s\(\d+\)$"))
                return 1;
            return 0;
        }

        internal static List<Group> Find(string game, string onlyRelation)
        {
            Dictionary<string, List<Relation>> byFunction = new Dictionary<string, List<Relation>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Relation> entry in (InternalDataManagement.AllRelations ?? new Dictionary<string, Relation>()).OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                Relation relation = entry.Value;
                Bind main;
                if (relation == null || !InternalDataManagement.AllBinds.TryGetValue(entry.Key, out main) || !MultiBind.IsBound(relation, main) || !HasGame(relation, game))
                    continue;
                string function = Function(relation);
                if (function.Length == 0)
                    continue;
                string key = (relation.ISAXIS ? "A\n" : "B\n") + function;
                List<Relation> list;
                if (!byFunction.TryGetValue(key, out list))
                    byFunction[key] = list = new List<Relation>();
                list.Add(relation);
            }

            List<Group> groups = new List<Group>();
            foreach (List<Relation> members in byFunction.Values.Where(l => l.Count > 1))
            {
                List<Relation> ordered = members.OrderBy(r => CopyRank(r.NAME)).ThenBy(r => (r.NAME ?? string.Empty).Length).ThenBy(r => r.NAME, StringComparer.OrdinalIgnoreCase).ToList();
                if (onlyRelation != null && !ordered.Any(r => string.Equals(r.NAME, onlyRelation, StringComparison.OrdinalIgnoreCase)))
                    continue;
                Group group = new Group { Target = ordered[0] };
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<string> inputs = new List<string>();
                foreach (Relation member in ordered)
                {
                    bool adds = false;
                    foreach (Bind bind in new[] { InternalDataManagement.AllBinds[member.NAME] }.Concat(MultiBind.Extras(member)).Where(b => MultiBind.IsBound(member, b)))
                    {
                        if (seen.Add(MultiBind.Signature(member, bind)))
                        {
                            inputs.Add(bind.Joystick + "::" + MultiBind.InputOf(member, bind));
                            adds = true;
                        }
                    }
                    if (member != group.Target && (adds || true))
                        group.Others.Add(member);
                }
                // Only relations on other inputs: copies on the very same input are "relations merge-copies"'s job.
                if (inputs.Count < 2)
                    continue;
                group.Info = new EngineJoinInputsGroup
                {
                    Relation = group.Target.NAME,
                    Joined = group.Others.Select(o => o.NAME).ToList(),
                    Inputs = inputs,
                    IsAxis = group.Target.ISAXIS,
                    Actions = Function(group.Target).Split('\n').Length
                };
                groups.Add(group);
            }
            return groups.OrderBy(g => g.Target.NAME, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static EngineJoinInputsResult Run(string game, string onlyRelation, bool apply)
        {
            EngineJoinInputsResult result = new EngineJoinInputsResult { DryRun = !apply };
            List<Group> groups = Find(game, onlyRelation);
            result.Groups = groups.Select(g => g.Info).ToList();
            if (!apply)
                return result;
            foreach (Group group in groups)
            {
                Relation target = group.Target;
                Bind targetMain = InternalDataManagement.AllBinds[target.NAME];
                HashSet<string> have = new HashSet<string>(new[] { targetMain }.Concat(MultiBind.Extras(target)).Where(b => MultiBind.IsBound(target, b)).Select(b => MultiBind.Signature(target, b)), StringComparer.OrdinalIgnoreCase);
                foreach (Relation other in group.Others)
                {
                    Bind otherMain;
                    InternalDataManagement.AllBinds.TryGetValue(other.NAME, out otherMain);
                    foreach (Bind bind in new[] { otherMain }.Concat(MultiBind.Extras(other)).Where(b => MultiBind.IsBound(other, b)))
                    {
                        if (!have.Add(MultiBind.Signature(other, bind)))
                            continue;
                        MultiBind.Add(target, bind.CopyAsExtra(target));
                        result.InputsAdded++;
                    }
                    foreach (string groupName in other.Groups ?? new List<string>())
                    {
                        if (!string.IsNullOrWhiteSpace(groupName) && !target.Groups.Contains(groupName, StringComparer.OrdinalIgnoreCase))
                            target.Groups.Add(groupName);
                    }
                    InternalDataManagement.AllRelations.Remove(other.NAME);
                    InternalDataManagement.AllBinds.Remove(other.NAME);
                    result.RelationsRemoved++;
                }
            }
            result.Applied = groups.Count > 0;
            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();
            return result;
        }

        // The note an import adds with the setting on: relations of the game that do the same on other inputs.
        internal static string ImportOffer(string game)
        {
            if (!MultiBind.Enabled)
                return null;
            List<Group> groups;
            try
            {
                groups = Find(game, null);
            }
            catch (Exception ex)
            {
                JoyPro.Logging.JoyProLog.Warn("MultiBind", "Join-inputs check after the import failed", ex);
                return null;
            }
            if (groups.Count == 0)
                return null;
            return groups.Count.ToString(CultureInfo.InvariantCulture) + " function(s) are bound on several inputs as separate relations (" +
                   string.Join(", ", groups.Take(3).Select(g => "'" + g.Target.NAME + "'")) + (groups.Count > 3 ? ", ..." : string.Empty) +
                   "): keep each as one relation with several inputs? engine relations join-inputs --game " + game + " --apply";
        }

        internal static string ToText(EngineJoinInputsResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine((result.DryRun ? "Would join: " : "Joined: ") + result.Groups.Count.ToString(CultureInfo.InvariantCulture) + " relation group(s)");
            foreach (EngineJoinInputsGroup group in result.Groups)
                sb.AppendLine("  - '" + group.Relation + "' keeps " + string.Join(", ", group.Inputs) + (group.Joined.Count > 0 ? " (from " + string.Join(", ", group.Joined.Select(j => "'" + j + "'")) + ")" : string.Empty));
            if (result.Applied)
                sb.AppendLine("Relations removed: " + result.RelationsRemoved.ToString(CultureInfo.InvariantCulture) + ", inputs added: " + result.InputsAdded.ToString(CultureInfo.InvariantCulture));
            foreach (string warning in result.Warnings)
                sb.AppendLine("Warning: " + warning);
            return sb.ToString();
        }
    }
}
