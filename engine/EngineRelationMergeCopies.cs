using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine
{
    public sealed class EngineMergeCopiesFamily
    {
        // The relation the copies go into ("<name>", else the lowest "<name>__N").
        public string Relation { get; set; } = string.Empty;
        public List<string> Copies { get; set; } = new List<string>();
        public string Device { get; set; } = string.Empty;
        public string Input { get; set; } = string.Empty;
        public bool IsAxis { get; set; }
        public List<string> Modifiers { get; set; } = new List<string>();
        // Aircraft of the copies ("game|plane"), and those the relation gets that it did not have.
        public List<string> Aircraft { get; set; } = new List<string>();
        public int NewAircraft { get; set; }
    }

    public sealed class EngineMergeCopiesResult
    {
        public bool DryRun { get; set; }
        public bool Applied { get; set; }
        public int Count { get; set; }
        public int CopiesRemoved { get; set; }
        public int RelationsBefore { get; set; }
        public int RelationsAfter { get; set; }
        public int BindsBefore { get; set; }
        public int BindsAfter { get; set; }
        public List<EngineMergeCopiesFamily> Families { get; set; } = new List<EngineMergeCopiesFamily>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    // Relations an import copied ("<name>", "<name>__1", "<name>__2", ...) that have the same bind: the same device,
    // input, axis/button and modifiers. A DCS import of a device Windows had given new IDs over time made one copy per
    // aircraft (fixed in the import); this merges such copies back into one relation: the union of the nodes and
    // aircraft, the groups, and each copy's axis tuning kept as the tuning of its aircraft (BindTuning.PlaneScope).
    // Within a copy family the same device under another instance GUID counts as the same device, unless two or more
    // of those GUIDs are connected at once (real twins); the merged bind is on the connected GUID when one is.
    internal static class EngineRelationMergeCopies
    {
        internal sealed class Member
        {
            public Relation Relation;
            public Bind Bind;
            public int Order;
        }

        internal sealed class Plan
        {
            public Relation Target;
            public Bind TargetBind;
            public List<Member> Copies = new List<Member>();
            public string Device;
            public EngineMergeCopiesFamily Family;
        }

        // The copy families to merge (all, or the one of a relation name: "<name>" or one of its copies).
        internal static List<Plan> Find(string onlyName = null)
        {
            Dictionary<string, Relation> relations = InternalDataManagement.AllRelations ?? new Dictionary<string, Relation>();
            Dictionary<string, Bind> binds = InternalDataManagement.AllBinds ?? new Dictionary<string, Bind>();
            string onlyBase = string.IsNullOrWhiteSpace(onlyName) ? null : EngineDcsImport.StripCopySuffix(onlyName);

            // Base name -> members with a bind; only names that have a copy.
            Dictionary<string, List<Member>> byBase = new Dictionary<string, List<Member>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> entry in relations)
            {
                Relation relation = entry.Value;
                if (relation == null)
                    continue;
                string name = entry.Key;
                string baseName = EngineDcsImport.StripCopySuffix(name);
                if (onlyBase != null && !string.Equals(baseName, onlyBase, StringComparison.OrdinalIgnoreCase))
                    continue;
                Bind bind;
                if (!binds.TryGetValue(name, out bind) || bind == null)
                    bind = relation.bind;
                if (bind == null || string.IsNullOrWhiteSpace(bind.Joystick) || string.IsNullOrWhiteSpace(relation.ISAXIS ? bind.JAxis : bind.JButton))
                    continue;
                List<Member> list;
                if (!byBase.TryGetValue(baseName, out list))
                    byBase[baseName] = list = new List<Member>();
                list.Add(new Member { Relation = relation, Bind = bind, Order = CopyNumber(name) });
            }

            HashSet<Guid> connected = null;
            List<Plan> plans = new List<Plan>();
            foreach (KeyValuePair<string, List<Member>> family in byBase.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (family.Value.Count < 2 || !family.Value.Any(m => EngineDcsImport.IsCopyName(m.Relation.NAME)))
                    continue;
                // The same device under several GUIDs in this family: one device unless two or more are connected.
                Dictionary<string, HashSet<Guid>> guidsByName = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
                foreach (Member m in family.Value)
                {
                    Guid guid;
                    if (!DcsDeviceNames.TryGetInstanceGuid(m.Bind.Joystick, out guid))
                        continue;
                    string deviceName = DeviceNameOf(m.Bind.Joystick);
                    HashSet<Guid> set;
                    if (!guidsByName.TryGetValue(deviceName, out set))
                        guidsByName[deviceName] = set = new HashSet<Guid>();
                    set.Add(guid);
                }
                HashSet<string> twins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, HashSet<Guid>> named in guidsByName.Where(n => n.Value.Count > 1))
                {
                    if (connected == null)
                        connected = ConnectedGuids();
                    if (named.Value.Count(connected.Contains) >= 2)
                        twins.Add(named.Key);
                }

                foreach (IGrouping<string, Member> same in family.Value.GroupBy(m => BindKey(m, twins), StringComparer.Ordinal))
                {
                    List<Member> members = same.OrderBy(m => m.Order).ThenBy(m => m.Relation.NAME, StringComparer.OrdinalIgnoreCase).ToList();
                    if (members.Count < 2)
                        continue;
                    Member target = members[0];
                    Plan plan = new Plan { Target = target.Relation, TargetBind = target.Bind, Copies = members.Skip(1).ToList() };
                    plan.Device = target.Bind.Joystick;
                    if (connected != null && guidsByName.ContainsKey(DeviceNameOf(target.Bind.Joystick)) && !twins.Contains(DeviceNameOf(target.Bind.Joystick)))
                    {
                        // The connected GUID, else the one most of the copies have (the relation's on a tie).
                        Member live = members.FirstOrDefault(m =>
                        {
                            Guid g;
                            return DcsDeviceNames.TryGetInstanceGuid(m.Bind.Joystick, out g) && connected.Contains(g);
                        });
                        plan.Device = live != null
                            ? live.Bind.Joystick
                            : members.GroupBy(m => m.Bind.Joystick, StringComparer.OrdinalIgnoreCase)
                                .OrderByDescending(g => g.Count())
                                .ThenBy(g => string.Equals(g.Key, target.Bind.Joystick, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                                .First().First().Bind.Joystick;
                    }
                    plan.Family = Describe(plan);
                    plans.Add(plan);
                }
            }
            return plans;
        }

        // "<name>" = 0, "<name>__N" = N.
        private static int CopyNumber(string name)
        {
            if (!EngineDcsImport.IsCopyName(name))
                return 0;
            string tail = name.Substring(name.LastIndexOf("__", StringComparison.Ordinal) + 2);
            int n;
            return int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : int.MaxValue;
        }

        private static string DeviceNameOf(string device)
        {
            string value = (device ?? string.Empty).Trim();
            int split = value.LastIndexOf(" {", StringComparison.Ordinal);
            return split > 0 && value.EndsWith("}", StringComparison.Ordinal) ? value.Substring(0, split).Trim() : value;
        }

        private static string BindKey(Member m, HashSet<string> twins)
        {
            string deviceName = DeviceNameOf(m.Bind.Joystick);
            string device = twins.Contains(deviceName) ? m.Bind.Joystick : deviceName;
            return EngineDcsImport.FamilyKey(string.Empty, device, m.Relation.ISAXIS ? m.Bind.JAxis : m.Bind.JButton, m.Relation.ISAXIS, m.Bind.AllReformers);
        }

        private static HashSet<Guid> ConnectedGuids()
        {
            HashSet<Guid> connected = new HashSet<Guid>();
            try
            {
                foreach (JoyPro.Engine.Games.GameDevice device in JoyPro.Engine.Games.GameDeviceDirectory.Discover(new List<string>()).Devices)
                {
                    if (!device.Connected)
                        continue;
                    Guid guid = device.InstanceGuid;
                    if (guid == Guid.Empty)
                        DcsDeviceNames.TryGetInstanceGuid(device.JoyProName, out guid);
                    if (guid != Guid.Empty)
                        connected.Add(guid);
                }
            }
            catch (Exception)
            {
            }
            return connected;
        }

        private static IEnumerable<KeyValuePair<string, string>> ActivePlanes(Relation relation)
        {
            foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
            {
                if (node == null || node.AIRCRAFT == null)
                    continue;
                string game = string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game;
                foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT)
                {
                    if (plane.Value && !string.IsNullOrWhiteSpace(plane.Key))
                        yield return new KeyValuePair<string, string>(game, plane.Key);
                }
            }
        }

        private static bool HasActive(Relation relation, string id, string game, string plane)
        {
            foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
            {
                if (node == null || !string.Equals(node.ID, id, StringComparison.Ordinal) ||
                    !string.Equals(string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game, game, StringComparison.OrdinalIgnoreCase))
                    continue;
                bool on;
                return node.AIRCRAFT != null && node.AIRCRAFT.TryGetValue(plane, out on) && on;
            }
            return false;
        }

        private static EngineMergeCopiesFamily Describe(Plan plan)
        {
            HashSet<string> before = new HashSet<string>(ActivePlanes(plan.Target).Select(p => p.Key + "|" + p.Value), StringComparer.OrdinalIgnoreCase);
            HashSet<string> copies = new HashSet<string>(plan.Copies.SelectMany(c => ActivePlanes(c.Relation)).Select(p => p.Key + "|" + p.Value), StringComparer.OrdinalIgnoreCase);
            return new EngineMergeCopiesFamily
            {
                Relation = plan.Target.NAME,
                Copies = plan.Copies.Select(c => c.Relation.NAME).ToList(),
                Device = plan.Device ?? string.Empty,
                Input = (plan.Target.ISAXIS ? plan.TargetBind.JAxis : plan.TargetBind.JButton) ?? string.Empty,
                IsAxis = plan.Target.ISAXIS,
                Modifiers = (plan.TargetBind.AllReformers ?? new List<string>())
                    .Select(r => (r ?? string.Empty).Split('§')[0].Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Aircraft = copies.Union(before, StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList(),
                NewAircraft = copies.Count(c => !before.Contains(c))
            };
        }

        // Merges the copies of the plans into their relations and removes them; returns the copies removed.
        internal static int Apply(List<Plan> plans)
        {
            int removed = 0;
            foreach (Plan plan in plans)
            {
                Relation target = plan.Target;
                Bind targetBind = plan.TargetBind;
                if (!string.Equals(targetBind.Joystick, plan.Device, StringComparison.OrdinalIgnoreCase))
                {
                    string oldName = DeviceNameOf(targetBind.Joystick);
                    targetBind.Joystick = plan.Device;
                    targetBind.JoyBackup = plan.Device;
                    // Modifiers on the same device follow it.
                    if (targetBind.AllReformers != null)
                    {
                        targetBind.AllReformers = targetBind.AllReformers.Select(r =>
                        {
                            string[] parts = (r ?? string.Empty).Split('§');
                            if (parts.Length >= 2 && string.Equals(DeviceNameOf(parts[1]), oldName, StringComparison.OrdinalIgnoreCase))
                                parts[1] = plan.Device;
                            return string.Join("§", parts);
                        }).ToList();
                    }
                }

                foreach (Member copy in plan.Copies)
                {
                    Relation source = copy.Relation;
                    // Axis tuning: an aircraft the relation gets from the copy keeps the copy's tuning when it differs.
                    if (target.ISAXIS)
                    {
                        foreach (RelationItem node in source.NODES ?? new List<RelationItem>())
                        {
                            if (node == null || node.AIRCRAFT == null)
                                continue;
                            string game = string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game;
                            foreach (KeyValuePair<string, bool> plane in node.AIRCRAFT.Where(p => p.Value))
                            {
                                if (HasActive(target, node.ID, game, plane.Key))
                                    continue;
                                AxisTuning mine = BindTuning.ResolveWithoutDetent(copy.Bind, game, plane.Key).Tuning;
                                AxisTuning theirs = BindTuning.ResolveWithoutDetent(targetBind, game, plane.Key).Tuning;
                                if (mine != null && (theirs == null || mine.Format() != theirs.Format()))
                                    BindTuning.Set(targetBind, BindTuning.PlaneScope(game, plane.Key), mine);
                            }
                        }
                    }
                    foreach (RelationItem node in source.NODES ?? new List<RelationItem>())
                    {
                        if (node != null)
                            target.IncludeRelationItem(node.Copy());
                    }
                    if (target.Groups == null)
                        target.Groups = new List<string>();
                    foreach (string group in source.Groups ?? new List<string>())
                    {
                        if (!string.IsNullOrWhiteSpace(group) && !target.Groups.Any(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase)))
                            target.Groups.Add(group.Trim());
                    }
                    if (string.IsNullOrWhiteSpace(target.Function) && !string.IsNullOrWhiteSpace(source.Function))
                        target.Function = source.Function;
                    if (string.IsNullOrWhiteSpace(targetBind.AdditionalImportInfo) && !string.IsNullOrWhiteSpace(copy.Bind.AdditionalImportInfo))
                        targetBind.AdditionalImportInfo = copy.Bind.AdditionalImportInfo;

                    if (InternalDataManagement.AllRelations.Remove(source.NAME))
                        removed++;
                    InternalDataManagement.AllBinds.Remove(source.NAME);
                }
                target.bind = targetBind;
                target.RecalculateElementCount();
            }
            return removed;
        }

        internal static string ToText(EngineMergeCopiesResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Copied relations with the same input");
            sb.AppendLine("Families: " + result.Count.ToString(CultureInfo.InvariantCulture) + (result.DryRun ? " (dry run, nothing changed)" : string.Empty));
            sb.AppendLine("Copies " + (result.Applied ? "merged" : "to merge") + ": " + (result.Applied ? result.CopiesRemoved : result.Families.Sum(f => f.Copies.Count)).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Relations: " + result.RelationsBefore.ToString(CultureInfo.InvariantCulture) + " -> " + result.RelationsAfter.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Binds: " + result.BindsBefore.ToString(CultureInfo.InvariantCulture) + " -> " + result.BindsAfter.ToString(CultureInfo.InvariantCulture));
            foreach (EngineMergeCopiesFamily family in result.Families)
            {
                sb.AppendLine("- " + family.Relation + " | " + family.Device + " " + family.Input +
                              (family.Modifiers.Count > 0 ? " + " + string.Join(" + ", family.Modifiers) : string.Empty) +
                              " | copies=" + family.Copies.Count.ToString(CultureInfo.InvariantCulture) +
                              " | aircraft=" + family.Aircraft.Count.ToString(CultureInfo.InvariantCulture) +
                              " (+" + family.NewAircraft.ToString(CultureInfo.InvariantCulture) + " new)");
            }
            foreach (string warning in result.Warnings)
                sb.AppendLine("Warning: " + warning);
            return sb.ToString();
        }
    }

    internal static partial class Program
    {
        // relations merge-copies [--dry-run] [--name <relation>]: merges relations an import copied ("<name>__N") that
        // have the same bind as "<name>" back into one (EngineRelationMergeCopies). One profile version (one undo step).
        private static int RelationMergeCopiesCommand(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            bool dryRun = PopFlag(args, "--dry-run");
            string name = PopOptionValue(args, "--name");
            if (args.Count > 0)
                return Fail("Unknown relations merge-copies argument(s): " + string.Join(" ", args) + ". Usage: relations merge-copies [--dry-run] [--name <relation>]");

            EngineMergeCopiesResult result = new EngineMergeCopiesResult { DryRun = dryRun };
            result.RelationsBefore = InternalDataManagement.AllRelations == null ? 0 : InternalDataManagement.AllRelations.Count;
            result.BindsBefore = InternalDataManagement.AllBinds == null ? 0 : InternalDataManagement.AllBinds.Count;
            List<EngineRelationMergeCopies.Plan> plans = EngineRelationMergeCopies.Find(name);
            result.Families = plans.Select(p => p.Family).ToList();
            result.Count = plans.Count;
            if (!dryRun && plans.Count > 0)
            {
                result.CopiesRemoved = EngineRelationMergeCopies.Apply(plans);
                result.Applied = true;
                InternalDataManagement.ResyncBindsToMods();
                InternalDataManagement.ResyncRelations();
                InternalDataManagement.RecalcFigures();
                if (string.IsNullOrWhiteSpace(EngineProfileHistory.Note))
                    EngineProfileHistory.Note = "Merging copied relations";
            }
            else if (!dryRun)
            {
                CommandChangedNothing = true;
            }
            int copies = plans.Sum(p => p.Copies.Count);
            result.RelationsAfter = result.Applied ? InternalDataManagement.AllRelations.Count : result.RelationsBefore - copies;
            result.BindsAfter = result.Applied ? InternalDataManagement.AllBinds.Count : result.BindsBefore - copies;
            EmitPayload(textOutput ? EngineRelationMergeCopies.ToText(result) : EngineSerialization.ToJson(result), outPath);
            return 0;
        }
    }
}
