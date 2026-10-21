using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    // The function a relation stands for (docs/relation-builder.md "The function chip"), one rule for the relation
    // editor's chip, Suggest missing, AI fill and Auto Assign:
    //   set      - the user picked it (Relation.Function = a catalog slot id);
    //   cleared  - the user said "None" (Relation.Function = "none"): no function, no function-based suggestions;
    //   named    - the relation is named after the function (Auto Group names relations so, optional "[...]" suffix);
    //   covered  - (nearly) all of its switched-on actions are that function: at least CoverShare of the actions the
    //              catalog knows (a catalog substitute for the function counts as it), and at least two of them
    //              (or the relation's only action);
    //   mixed    - the catalog knows its actions as different functions and none covers them: the relation is a
    //              button position (like the real cockpit: "Throttle - Boat - Aft" holds Magic in one aircraft, the
    //              drag chute in another); each aircraft uses it for its own function, so there is no function to
    //              suggest from;
    //   unknown  - the catalog knows too little of it.
    // A majority is not enough: a function label over a relation whose aircraft use it for different things is wrong.
    internal static class EngineRelationPurpose
    {
        public const double CoverShare = 0.8;
        public const string NoneValue = "none";

        internal enum Kind
        {
            Unknown,
            Set,
            Cleared,
            Named,
            Covered,
            Mixed
        }

        internal sealed class Verdict
        {
            public Kind Kind;
            public EngineAutoGroup.Slot Slot;
            // Switched-on actions (per aircraft), those the catalog knows, those that are the slot, distinct functions.
            public int Total;
            public int Known;
            public int Agree;
            public int Distinct;

            public bool HasFunction { get { return Slot != null; } }

            public bool IsMixed { get { return Kind == Kind.Mixed; } }

            // No function-based suggestion may come from this relation (a button position, or the user said none).
            public bool NoFunction { get { return Kind == Kind.Mixed || Kind == Kind.Cleared; } }

            public double Share { get { return Known == 0 ? 0 : Math.Round((double)Agree / Known, 2); } }

            public string State { get { return Kind.ToString().ToLowerInvariant(); } }
        }

        // The catalog's functions per action: "GAME|id|plane" -> slot ids, the action's own rows and the rows that file
        // it as a substitute for a function the aircraft lacks.
        internal sealed class Index
        {
            public readonly Dictionary<string, EngineAutoGroup.Slot> Slots;
            private readonly Dictionary<string, string> byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, HashSet<string>> byAction = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            public Index(Dictionary<string, EngineAutoGroup.Slot> slots, IEnumerable<EngineAutoGroup.Row> rows)
            {
                Slots = slots ?? new Dictionary<string, EngineAutoGroup.Slot>(StringComparer.OrdinalIgnoreCase);
                foreach (EngineAutoGroup.Slot slot in Slots.Values.Where(s => !byName.ContainsKey(s.Name)))
                    byName[slot.Name] = slot.Id;
                foreach (EngineAutoGroup.Row row in rows ?? Enumerable.Empty<EngineAutoGroup.Row>())
                {
                    if (row == null || string.Equals(row.Status, "rejected", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string game = EngineRelationSuggest.GameKey(row.Game ?? string.Empty);
                    string key = game + "|" + EngineRelationSuggest.NodeId(game, row.Action) + "|" + (game == "DCS" ? row.Plane : game);
                    HashSet<string> set;
                    if (!byAction.TryGetValue(key, out set))
                        byAction[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(row.Slot);
                }
            }

            public EngineAutoGroup.Slot ByName(string name)
            {
                string id;
                return name != null && byName.TryGetValue(name, out id) && Slots.ContainsKey(id) ? Slots[id] : null;
            }

            // A slot by id, else by its name ("CMS FWD (Start Program or just Flare)").
            public EngineAutoGroup.Slot Find(string idOrName)
            {
                EngineAutoGroup.Slot slot;
                if (string.IsNullOrWhiteSpace(idOrName))
                    return null;
                if (Slots.TryGetValue(idOrName.Trim(), out slot))
                    return slot;
                slot = ByName(idOrName.Trim());
                if (slot != null)
                    return slot;
                // "Flaps Down" for "FlapsDown" or "flaps_down": letters and numbers only.
                string wanted = Letters(idOrName);
                return Slots.Values.FirstOrDefault(s => Letters(s.Name) == wanted || Letters(EngineAutoGroup.ReadableName(s.Name)) == wanted || Letters(s.Id) == wanted);
            }

            private static string Letters(string text)
            {
                return Regex.Replace(text ?? string.Empty, "[^A-Za-z0-9]", string.Empty).ToLowerInvariant();
            }

            public IEnumerable<string> Of(string game, string id, string plane)
            {
                HashSet<string> set;
                return byAction.TryGetValue(game + "|" + EngineRelationSuggest.NodeId(game, id) + "|" + plane, out set) ? set : Enumerable.Empty<string>();
            }
        }

        private static Index cached;

        // The function catalog, read once per run.
        internal static Index Load(List<string> warnings)
        {
            if (cached != null)
                return cached;
            EngineAutoGroupResult errors = new EngineAutoGroupResult();
            Dictionary<string, EngineAutoGroup.Slot> slots;
            Dictionary<string, EngineAutoGroup.Aircraft> aircraft;
            List<EngineAutoGroup.Row> rows;
            if (!EngineAutoGroup.TryLoadCatalog(EngineNlGroup.FunctionsFolder(), errors, out slots, out aircraft, out rows))
            {
                if (warnings != null)
                    warnings.AddRange(errors.Errors);
                return new Index(null, null);
            }
            cached = new Index(slots, rows);
            return cached;
        }

        internal static string BaseName(string relationName)
        {
            return Regex.Replace(relationName ?? string.Empty, @"\s*\[[^\]]*\]$", string.Empty);
        }

        public static Verdict Judge(Relation relation, Index index)
        {
            Verdict verdict = new Verdict();
            if (relation == null || index == null)
                return verdict;
            string own = (relation.Function ?? string.Empty).Trim();
            if (string.Equals(own, NoneValue, StringComparison.OrdinalIgnoreCase))
            {
                verdict.Kind = Kind.Cleared;
                Count(relation, index, verdict, null);
                return verdict;
            }
            EngineAutoGroup.Slot chosen = own.Length > 0 ? index.Find(own) : null;
            if (chosen != null)
            {
                verdict.Kind = Kind.Set;
                verdict.Slot = chosen;
                Count(relation, index, verdict, chosen);
                return verdict;
            }
            EngineAutoGroup.Slot named = index.ByName(BaseName(relation.NAME));
            if (named != null && named.Axis == relation.ISAXIS)
            {
                verdict.Kind = Kind.Named;
                verdict.Slot = named;
                Count(relation, index, verdict, named);
                return verdict;
            }
            Dictionary<string, int> votes = Count(relation, index, verdict, null);
            if (verdict.Known == 0)
                return verdict;
            KeyValuePair<string, int> top = votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal).First();
            verdict.Agree = top.Value;
            bool enough = verdict.Known >= 2 || verdict.Total == 1;
            if (enough && top.Value >= CoverShare * verdict.Known && index.Slots.ContainsKey(top.Key))
            {
                verdict.Kind = Kind.Covered;
                verdict.Slot = index.Slots[top.Key];
                return verdict;
            }
            verdict.Kind = top.Value < CoverShare * verdict.Known && verdict.Distinct >= 2 ? Kind.Mixed : Kind.Unknown;
            return verdict;
        }

        // Votes per function over the relation's switched-on actions (per aircraft; other games once), of the
        // relation's kind (axis or button) only.
        private static Dictionary<string, int> Count(Relation relation, Index index, Verdict verdict, EngineAutoGroup.Slot slot)
        {
            Dictionary<string, int> votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (RelationItem node in relation.NODES ?? new List<RelationItem>())
            {
                if (node == null || string.IsNullOrWhiteSpace(node.ID))
                    continue;
                string game = EngineRelationSuggest.GameKey(node.Game ?? string.Empty);
                Dictionary<string, bool> aircraft = node.AIRCRAFT ?? new Dictionary<string, bool>();
                IEnumerable<string> planes = game == "DCS"
                    ? aircraft.Where(p => p.Value).Select(p => p.Key)
                    : (aircraft.Count == 0 || aircraft.Any(p => p.Value) ? new[] { game } : new string[0]);
                foreach (string plane in planes)
                {
                    verdict.Total++;
                    List<string> functions = index.Of(game, node.ID, plane)
                        .Where(s => index.Slots.ContainsKey(s) && index.Slots[s].Axis == relation.ISAXIS).ToList();
                    if (functions.Count == 0)
                        continue;
                    verdict.Known++;
                    foreach (string function in functions)
                        votes[function] = (votes.ContainsKey(function) ? votes[function] : 0) + 1;
                }
            }
            verdict.Distinct = votes.Count;
            if (slot != null)
                verdict.Agree = votes.ContainsKey(slot.Id) ? votes[slot.Id] : 0;
            return votes;
        }

        // ---------------------------------------------------------------------------------------------------------
        // CLI: relations function <relation> [--set <function>|--none|--auto]; relations function --list [--axis|--button] [--query text]
        // ---------------------------------------------------------------------------------------------------------

        public static EngineRelationFunctionResult Describe(Relation relation, Index index)
        {
            Verdict verdict = Judge(relation, index);
            return new EngineRelationFunctionResult
            {
                Relation = relation.NAME,
                IsAxis = relation.ISAXIS,
                State = verdict.State,
                Slot = verdict.Slot != null ? verdict.Slot.Id : string.Empty,
                SlotName = verdict.Slot != null ? EngineAutoGroup.ReadableName(verdict.Slot.Name) : string.Empty,
                SetByUser = verdict.Kind == Kind.Set || verdict.Kind == Kind.Cleared,
                Share = verdict.Share,
                Total = verdict.Total,
                Known = verdict.Known,
                Agree = verdict.Agree,
                Distinct = verdict.Distinct
            };
        }

        public static List<EngineRelationFunctionChoice> Choices(Index index, bool? axis, string query)
        {
            string[] words = (query ?? string.Empty).ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return index.Slots.Values
                .Where(s => !axis.HasValue || s.Axis == axis.Value)
                .Select(s => new EngineRelationFunctionChoice { Id = s.Id, Name = EngineAutoGroup.ReadableName(s.Name), IsAxis = s.Axis, Kind = s.Kind })
                .Where(c => words.All(w => c.Name.ToLowerInvariant().Contains(w) || c.Id.ToLowerInvariant().Contains(w)))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string ToText(EngineRelationFunctionResult result)
        {
            string what;
            switch (result.State)
            {
                case "set": what = "Function: " + result.SlotName + " (" + result.Slot + "), set by you"; break;
                case "cleared": what = "No function (set by you)"; break;
                case "named": what = "Function: " + result.SlotName + " (" + result.Slot + "), the relation's name"; break;
                case "covered": what = "Function: " + result.SlotName + " (" + result.Slot + "), " + Percent(result.Share) + " of its known actions"; break;
                case "mixed": what = "Different functions per aircraft (" + result.Distinct.ToString(CultureInfo.InvariantCulture) + " functions, the largest " + Percent(result.Share) + "): a button position"; break;
                default: what = "No function known (the catalog knows " + result.Known.ToString(CultureInfo.InvariantCulture) + " of " + result.Total.ToString(CultureInfo.InvariantCulture) + " actions)"; break;
            }
            return result.Relation + ": " + what;
        }

        private static string Percent(double share)
        {
            return Math.Round(share * 100).ToString(CultureInfo.InvariantCulture) + "%";
        }
    }

    public sealed class EngineRelationFunctionResult
    {
        public string Relation { get; set; }
        public bool IsAxis { get; set; }
        // set, cleared, named, covered, mixed, unknown (EngineRelationPurpose.Kind).
        public string State { get; set; }
        public string Slot { get; set; }
        public string SlotName { get; set; }
        public bool SetByUser { get; set; }
        public double Share { get; set; }
        public int Total { get; set; }
        public int Known { get; set; }
        public int Agree { get; set; }
        public int Distinct { get; set; }
        public List<EngineRelationFunctionChoice> Choices { get; set; }
    }

    public sealed class EngineRelationFunctionChoice
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsAxis { get; set; }
        public string Kind { get; set; }
    }

    // relations function <relation>                       the relation's function and how sure it is (EngineRelationPurpose)
    // relations function <relation> --set <function>      the user's choice: a catalog function (id or name)
    // relations function <relation> --none                no function (a button position: each aircraft its own)
    // relations function <relation> --auto                back to automatic (its name or what its actions are)
    // relations function --list [--axis|--button] [--query text]   the catalog functions to pick from
    internal static partial class Program
    {
        private static int RelationFunctionCommand(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            bool list = PopFlag(args, "--list");
            bool axisOnly = PopFlag(args, "--axis");
            bool buttonOnly = PopFlag(args, "--button");
            string query = PopOptionValue(args, "--query");
            string set = PopOptionValue(args, "--set");
            bool none = PopFlag(args, "--none");
            bool auto = PopFlag(args, "--auto");
            List<string> warnings = new List<string>();
            EngineRelationPurpose.Index index = EngineRelationPurpose.Load(warnings);
            if (list)
            {
                if (args.Count > 0)
                    return Fail("Unknown relations function argument(s): " + string.Join(" ", args));
                bool? axis = axisOnly == buttonOnly ? (bool?)null : axisOnly;
                List<EngineRelationFunctionChoice> choices = EngineRelationPurpose.Choices(index, axis, query);
                EmitPayload(textOutput
                    ? string.Join(Environment.NewLine, choices.Select(c => c.Id + "	" + c.Name + (c.IsAxis ? " (axis)" : string.Empty)))
                    : EngineSerialization.ToJson(choices), outPath);
                return 0;
            }
            if (args.Count != 1)
                return Fail("relations function <relation> [--set <function>|--none|--auto]; relations function --list [--axis|--button] [--query text]");
            if ((set != null ? 1 : 0) + (none ? 1 : 0) + (auto ? 1 : 0) > 1)
                return Fail("Use one of --set, --none and --auto.");
            List<string> candidates;
            string relationName;
            if (!TryResolveRelationName(args[0], out relationName, out candidates))
                return FailResolution("relation", args[0], candidates);
            Relation relation = InternalDataManagement.AllRelations[relationName];
            if (set != null)
            {
                EngineAutoGroup.Slot slot = index.Find(set);
                if (slot == null)
                    return Fail("Unknown function: " + set + " (relations function --list shows them).");
                if (slot.Axis != relation.ISAXIS)
                    return Fail("'" + slot.Name + "' is " + (slot.Axis ? "an axis" : "a button") + " function; '" + relation.NAME + "' is " + (relation.ISAXIS ? "an axis" : "a button") + " relation.");
                CommandChangedNothing = string.Equals(relation.Function, slot.Id, StringComparison.OrdinalIgnoreCase);
                relation.Function = slot.Id;
            }
            else if (none)
            {
                CommandChangedNothing = string.Equals(relation.Function, EngineRelationPurpose.NoneValue, StringComparison.OrdinalIgnoreCase);
                relation.Function = EngineRelationPurpose.NoneValue;
            }
            else if (auto)
            {
                CommandChangedNothing = string.IsNullOrEmpty(relation.Function);
                relation.Function = null;
            }
            EngineRelationFunctionResult result = EngineRelationPurpose.Describe(relation, index);
            EmitPayload(textOutput ? EngineRelationPurpose.ToText(result) : EngineSerialization.ToJson(result), outPath);
            return 0;
        }
    }
}
