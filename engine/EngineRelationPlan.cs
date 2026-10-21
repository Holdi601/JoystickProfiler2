using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    // "Manage with AI" / "Edit with AI" (docs/ai-plans.md): a request becomes a PLAN of JoyPro's own relation
    // operations. The local model (or a few offline patterns) only proposes the plan; the engine checks every step
    // against the profile and the catalog, the app shows it with a checkbox per step, and the ticked steps run in one
    // engine call (one history step).
    //   relations plan ask "<request>" [--relation <name>] [--llm auto|on|off]   the plan, checked (nothing changes)
    //   relations plan apply --file <plan.json> [--relation <name>] [--dry-run]   runs the plan's steps
    // Steps: create {name, kind, describe}, delete {relations}, rename {relation, to}, add/remove {relation, describe
    // or games}, aircraft {relation, planes, on}, groups {relation, add, remove}, tuning {relation, curvature,
    // deadzone, saturation, invert}, fill {planes, relations} (a preview), check {relations} (unticks aircraft where an
    // action does something else). "describe" is found the way Group by description finds it (offline rules).
    internal static class EngineRelationPlan
    {
        public static readonly string[] Ops = { "create", "delete", "rename", "add", "remove", "aircraft", "groups", "tuning", "fill", "check" };

        // plan ask: the fill step may ask the local model as its second path.
        public static bool FillWithModel;

        public sealed class Op
        {
            public string Kind = string.Empty;
            public string Name = string.Empty;
            public string AxisKind = string.Empty;
            public string Describe = string.Empty;
            public string Relation = string.Empty;
            public string To = string.Empty;
            public List<string> Relations = new List<string>();
            public List<string> Planes = new List<string>();
            public List<string> Games = new List<string>();
            public List<string> Add = new List<string>();
            public List<string> Remove = new List<string>();
            // add: exact actions "GAME:id:plane" (what a fill preview hands back).
            public List<string> Specs = new List<string>();
            public bool? On;
            public double? Curvature;
            public double? Deadzone;
            public double? Saturation;
            public bool? Invert;

            public Dictionary<string, object> ToMap()
            {
                Dictionary<string, object> map = new Dictionary<string, object> { { "op", Kind } };
                if (Name.Length > 0) map["name"] = Name;
                if (AxisKind.Length > 0) map["kind"] = AxisKind;
                if (Describe.Length > 0) map["describe"] = Describe;
                if (Relation.Length > 0) map["relation"] = Relation;
                if (To.Length > 0) map["to"] = To;
                if (Relations.Count > 0) map["relations"] = Relations;
                if (Planes.Count > 0) map["planes"] = Planes;
                if (Games.Count > 0) map["games"] = Games;
                if (Add.Count > 0) map["add"] = Add;
                if (Remove.Count > 0) map["remove"] = Remove;
                if (Specs.Count > 0) map["specs"] = Specs;
                if (On.HasValue) map["on"] = On.Value;
                if (Curvature.HasValue) map["curvature"] = Curvature.Value;
                if (Deadzone.HasValue) map["deadzone"] = Deadzone.Value;
                if (Saturation.HasValue) map["saturation"] = Saturation.Value;
                if (Invert.HasValue) map["invert"] = Invert.Value;
                return map;
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Parsing
        // ------------------------------------------------------------------------------------------------------

        public static List<Op> Parse(string json, List<string> errors)
        {
            List<Op> ops = new List<Op>();
            object root;
            try
            {
                root = EngineSerialization.FromJson<object>(json ?? string.Empty);
            }
            catch (Exception ex)
            {
                errors.Add("The plan is no valid JSON: " + ex.Message);
                return ops;
            }
            object list = root;
            Dictionary<string, object> map = root as Dictionary<string, object>;
            if (map != null)
                list = EngineNlGroup.Get(map, "ops") ?? EngineNlGroup.Get(map, "steps");
            foreach (Dictionary<string, object> item in EngineNlGroup.Objects(list))
                ops.Add(ParseOp(item));
            return ops;
        }

        private static Op ParseOp(Dictionary<string, object> item)
        {
            Op op = new Op
            {
                Kind = EngineNlGroup.Text(item, "op").Trim().ToLowerInvariant(),
                Name = Clean(EngineNlGroup.Text(item, "name")),
                AxisKind = EngineNlGroup.Text(item, "kind").Trim().ToLowerInvariant(),
                Describe = EngineNlGroup.Text(item, "describe").Trim(),
                Relation = Clean(EngineNlGroup.Text(item, "relation")),
                To = Clean(EngineNlGroup.Text(item, "to")),
                Relations = Strings(item, "relations"),
                Planes = Strings(item, "planes"),
                Games = Strings(item, "games"),
                Add = Strings(item, "add"),
                Remove = Strings(item, "remove"),
                Specs = Strings(item, "specs"),
                On = Bool(item, "on"),
                Curvature = Number(item, "curvature"),
                Deadzone = Number(item, "deadzone"),
                Saturation = Number(item, "saturation"),
                Invert = Bool(item, "invert")
            };
            return op;
        }

        private static string Clean(string value)
        {
            return Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim().Trim('"', '\'');
        }

        private static List<string> Strings(Dictionary<string, object> item, string key)
        {
            object value = EngineNlGroup.Get(item, key);
            string single = value as string;
            if (single != null)
                return single.Trim().Length > 0 ? new List<string> { single.Trim() } : new List<string>();
            System.Collections.IEnumerable list = value as System.Collections.IEnumerable;
            if (list == null)
                return new List<string>();
            return list.Cast<object>().Select(o => Convert.ToString(o, CultureInfo.InvariantCulture) ?? string.Empty).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }

        private static bool? Bool(Dictionary<string, object> item, string key)
        {
            object value = EngineNlGroup.Get(item, key);
            if (value is bool)
                return (bool)value;
            bool parsed;
            if (value is string && bool.TryParse((string)value, out parsed))
                return parsed;
            return null;
        }

        private static double? Number(Dictionary<string, object> item, string key)
        {
            object value = EngineNlGroup.Get(item, key);
            if (value == null)
                return null;
            double parsed;
            if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return null;
        }

        // ------------------------------------------------------------------------------------------------------
        // Running the steps (in memory; the command decides whether the profile is saved)
        // ------------------------------------------------------------------------------------------------------

        public static EngineRelationPlanResult Execute(List<Op> ops, string scope, HashSet<int> only)
        {
            EngineRelationPlanResult result = new EngineRelationPlanResult { Scope = scope ?? string.Empty };
            string focus = scope ?? string.Empty;
            for (int i = 0; i < ops.Count; i++)
            {
                Op op = ops[i];
                EngineRelationPlanStep step = new EngineRelationPlanStep { Index = i, Op = op.Kind, Plan = op.ToMap() };
                result.Steps.Add(step);
                if (only != null && !only.Contains(i))
                {
                    step.Skipped = true;
                    step.Text = Describe(op);
                    continue;
                }
                try
                {
                    string error = focus.Length > 0 ? CheckScope(op, focus) : string.Empty;
                    if (error.Length == 0)
                        error = Run(op, step);
                    if (error.Length > 0)
                    {
                        step.Error = error;
                    }
                    else
                    {
                        step.Ok = true;
                        if (!step.Preview)
                            result.Changed = true;
                        if (focus.Length > 0 && op.Kind == "rename" && step.Relation.Length > 0)
                            focus = step.Relation;
                    }
                }
                catch (Exception ex)
                {
                    step.Error = ex.Message;
                }
                if (step.Text.Length == 0)
                    step.Text = Describe(op);
            }
            result.Scope = focus;
            return result;
        }

        // Edit with AI: nothing outside the open relation.
        private static string CheckScope(Op op, string focus)
        {
            if (op.Kind == "create" || op.Kind == "delete")
                return "Only the open relation \"" + focus + "\" can be changed here.";
            string target = Find(op.Relation.Length > 0 ? op.Relation : focus);
            if (target == null || !string.Equals(target, focus, StringComparison.Ordinal))
                return "Only the open relation \"" + focus + "\" can be changed here.";
            op.Relation = focus;
            return string.Empty;
        }

        internal static string Find(string name)
        {
            string wanted = Clean(name);
            if (wanted.Length == 0)
                return null;
            if (InternalDataManagement.AllRelations.ContainsKey(wanted))
                return wanted;
            return InternalDataManagement.AllRelations.Keys.FirstOrDefault(k => string.Equals(k, wanted, StringComparison.OrdinalIgnoreCase));
        }

        private static string Run(Op op, EngineRelationPlanStep step)
        {
            switch (op.Kind)
            {
                case "create": return Create(op, step);
                case "delete": return Delete(op, step);
                case "rename": return Rename(op, step);
                case "add": return Change(op, step, true);
                case "remove": return Change(op, step, false);
                case "aircraft": return Aircraft(op, step);
                case "groups": return Groups(op, step);
                case "tuning": return Tuning(op, step);
                case "fill": return Fill(op, step);
                case "check": return Check(op, step);
                default: return "Unknown step \"" + op.Kind + "\" (allowed: " + string.Join(", ", Ops) + ").";
            }
        }

        private static string Create(Op op, EngineRelationPlanStep step)
        {
            if (op.Describe.Length == 0)
                return "Say what the new relation does (describe).";
            List<EngineNlGroupNode> nodes;
            bool isAxis;
            string function;
            string problem = Resolve(op.Describe, op.AxisKind, out nodes, out isAxis, out function);
            if (problem.Length > 0)
                return problem;
            string name = op.Name.Length > 0 ? op.Name : function;
            if (name.Length == 0 || name.Length > 80)
                return "Give the new relation a name (at most 80 characters).";
            string clash = Find(name);
            if (clash != null)
                return "\"" + clash + "\" exists already.";
            Relation relation = new Relation { NAME = name, ISAXIS = isAxis, Groups = new List<string>(), NODES = new List<RelationItem>() };
            InternalDataManagement.AllRelations[name] = relation;
            EngineRelationSuggestApplyResult done = EngineRelationSuggest.Apply(name, nodes.Select(n => n.Spec).ToList(), new List<string>(), new List<string>(), new List<string>());
            step.Relation = name;
            step.Before = "-";
            step.After = Brief(relation);
            step.Text = "Create \"" + name + "\" (" + (isAxis ? "axis" : "button") + ") with " + op.Describe;
            step.Details = nodes.Select(NodeText).Take(60).ToList();
            if (done.Added == 0)
            {
                InternalDataManagement.AllRelations.Remove(name);
                return "Nothing in the catalog fits \"" + op.Describe + "\" (all of it may be in other relations already).";
            }
            return string.Empty;
        }

        private static string Delete(Op op, EngineRelationPlanStep step)
        {
            List<string> names = op.Relations.Count > 0 ? op.Relations : (op.Relation.Length > 0 ? new List<string> { op.Relation } : new List<string>());
            if (names.Count == 0)
                return "Name the relations to delete.";
            List<string> missing = new List<string>();
            List<string> deleted = new List<string>();
            foreach (string wanted in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string name = Find(wanted);
                if (name == null)
                {
                    missing.Add(wanted);
                    continue;
                }
                InternalDataManagement.AllRelations.Remove(name);
                InternalDataManagement.AllBinds.Remove(name);
                deleted.Add(name);
            }
            step.Text = "Delete " + deleted.Count.ToString(CultureInfo.InvariantCulture) + (deleted.Count == 1 ? " relation" : " relations");
            step.Before = deleted.Count.ToString(CultureInfo.InvariantCulture) + " relations";
            step.After = "-";
            step.Details = deleted;
            if (missing.Count > 0)
                step.Warnings.Add("Not found (left out): " + string.Join(", ", missing));
            return deleted.Count == 0 ? "None of these relations exists: " + string.Join(", ", missing) : string.Empty;
        }

        private static string Rename(Op op, EngineRelationPlanStep step)
        {
            string name = Find(op.Relation);
            if (name == null)
                return "Relation not found: " + op.Relation;
            string wanted = op.To.Length > 0 ? op.To : op.Name;
            if (wanted.Length == 0 || wanted.Length > 80)
                return "Give the new name (at most 80 characters).";
            string clash = Find(wanted);
            if (clash != null && clash != name)
                return "\"" + clash + "\" exists already.";
            string final = Program.RenameRelation(name, wanted);
            if (string.IsNullOrEmpty(final))
                return "Could not rename \"" + name + "\".";
            step.Relation = final;
            step.Text = "Rename \"" + name + "\" to \"" + final + "\"";
            step.Before = name;
            step.After = final;
            return string.Empty;
        }

        private static string Change(Op op, EngineRelationPlanStep step, bool add)
        {
            string name = Find(op.Relation);
            if (name == null)
                return "Relation not found: " + op.Relation;
            Relation relation = InternalDataManagement.AllRelations[name];
            string before = Brief(relation);
            step.Relation = name;
            List<string> specs = new List<string>();
            if (op.Specs.Count > 0)
            {
                List<string> unknown = new List<string>();
                foreach (string spec in op.Specs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string[] parts = spec.Split(':');
                    if (parts.Length < 3 || EngineIdMeanings.Find(EngineRelationSuggest.GameKey(parts[0]), parts[1], parts[parts.Length - 1].Split('|')[0]) == null)
                        unknown.Add(spec);
                    else
                        specs.Add(spec);
                }
                if (unknown.Count > 0)
                    step.Warnings.Add("Not in the catalog (left out): " + string.Join(", ", unknown.Take(10)));
                if (specs.Count == 0)
                    return "None of these actions is in the catalog.";
                step.Details = specs.Take(60).ToList();
                op.Describe = specs.Count.ToString(CultureInfo.InvariantCulture) + " actions";
            }
            else if (op.Describe.Length > 0)
            {
                List<EngineNlGroupNode> nodes;
                bool isAxis;
                string function;
                string problem = Resolve(op.Describe, relation.ISAXIS ? "axis" : "button", out nodes, out isAxis, out function);
                if (problem.Length > 0)
                    return problem;
                specs = nodes.Select(n => n.Spec).ToList();
                step.Details = nodes.Select(NodeText).Take(60).ToList();
            }
            else if (!add && op.Games.Count > 0)
            {
                HashSet<string> games = new HashSet<string>(op.Games.Select(GameKeyOf), StringComparer.OrdinalIgnoreCase);
                foreach (RelationItem node in (relation.NODES ?? new List<RelationItem>()).Where(n => n != null && games.Contains(EngineRelationSuggest.GameKey(n.Game ?? string.Empty))).ToList())
                    relation.NODES.Remove(node);
                step.Text = "Remove " + string.Join(", ", op.Games) + " from \"" + name + "\"";
                step.Before = before;
                step.After = Brief(relation);
                return before == step.After ? "\"" + name + "\" has nothing of " + string.Join(", ", op.Games) + "." : string.Empty;
            }
            else
            {
                return add ? "Say what to add (describe)." : "Say what to remove (describe or games).";
            }
            EngineRelationSuggestApplyResult done = add
                ? EngineRelationSuggest.Apply(name, specs, new List<string>(), new List<string>(), new List<string>())
                : EngineRelationSuggest.Apply(name, new List<string>(), new List<string>(), specs, new List<string>());
            step.Text = (add ? "Add " : "Remove ") + op.Describe + (add ? " to \"" : " from \"") + name + "\"";
            step.Before = before;
            step.After = Brief(relation);
            foreach (string error in done.Errors)
                step.Warnings.Add(error);
            int changed = add ? done.Added : done.Removed;
            if (changed == 0)
                return add ? "Nothing new to add to \"" + name + "\" for \"" + op.Describe + "\"." : "\"" + name + "\" has nothing of \"" + op.Describe + "\".";
            return string.Empty;
        }

        // fill: the named aircraft into every relation (or op.Relations) where they fit; only a preview, the app hands
        // back the ticked actions as add steps.
        private static string Fill(Op op, EngineRelationPlanStep step)
        {
            if (op.Planes.Count == 0)
                return "Name the aircraft (or games) to fill in.";
            List<string> unresolved = new List<string>();
            List<string> targets = new List<string>();
            foreach (string name in op.Planes)
            {
                List<string> found = EngineRelationFill.Resolve(name);
                if (found.Count == 0)
                    found = EngineRelationFill.FromText(name, new List<string>());
                if (found.Count == 0)
                    unresolved.Add(name);
                foreach (string t in found)
                    if (!targets.Contains(t, StringComparer.OrdinalIgnoreCase))
                        targets.Add(t);
            }
            if (targets.Count == 0)
                return "JoyPro does not know these aircraft: " + string.Join(", ", unresolved);
            List<string> scope = op.Relations.Select(Find).Where(n => n != null).ToList();
            if (op.Relation.Length > 0 && Find(op.Relation) != null)
                scope.Add(Find(op.Relation));
            EngineFillResult fill = EngineRelationFill.Run(targets, scope, FillWithModel, unresolved);
            step.Fill = fill;
            step.Text = "Fill " + PlaneList(targets) + " into " + (scope.Count > 0 ? string.Join(", ", scope) : "every relation where it fits");
            // The summary and the per-relation list are the fill's own preview (no before/after line).
            step.Warnings.AddRange(fill.Warnings.Take(5));
            if (fill.Note.Length > 0)
                step.Warnings.Add(fill.Note);
            step.Preview = true;
            return fill.Relations.Count == 0 ? "No relation gets anything for " + PlaneList(targets) + "." : string.Empty;
        }

        // A long aircraft list in a step's text: the first ones and how many more.
        private static string PlaneList(List<string> targets)
        {
            List<string> planes = targets.Select(EngineRelationFill.PlaneOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return planes.Count <= 12 ? string.Join(", ", planes) : string.Join(", ", planes.Take(8)) + " and " + (planes.Count - 8).ToString(CultureInfo.InvariantCulture) + " more";
        }

        // check: every action of the relations (all, op.Relations or op.Relation) against all its ticked aircraft: where
        // the same id does something else in an aircraft (relations check-aircraft), that aircraft is unticked. Nothing
        // found: a preview line only.
        private static string Check(Op op, EngineRelationPlanStep step)
        {
            List<string> names = op.Relations.Select(Find).Where(n => n != null).ToList();
            if (op.Relation.Length > 0 && Find(op.Relation) != null)
                names.Add(Find(op.Relation));
            List<string> missing = op.Relations.Where(n => Find(n) == null).ToList();
            if (names.Count == 0 && missing.Count > 0)
                return "Relation not found: " + string.Join(", ", missing);
            List<EngineAircraftCheckResult> results = names.Count == 0
                ? new List<EngineAircraftCheckResult> { EngineIdMeanings.Check(null, true, false) }
                : names.Distinct(StringComparer.Ordinal).Select(n => EngineIdMeanings.Check(n, true, false)).ToList();
            int relations = results.Sum(r => r.Relations);
            int dropped = results.Sum(r => r.Dropped);
            List<EngineAircraftCheckNode> found = results.SelectMany(r => r.Found).ToList();
            int otherKind = results.Sum(r => r.OtherKindNodes);
            int sameFunction = results.Sum(r => r.AircraftSameFunction);
            string what = names.Count == 0 ? "every relation (" + relations.ToString(CultureInfo.InvariantCulture) + ")" : string.Join(", ", names.Select(n => "\"" + n + "\""));
            if (names.Count == 1)
                step.Relation = names[0];
            foreach (EngineAircraftCheckNode node in found.Where(f => f.Drop.Count > 0).Take(80))
            {
                string meanings = string.Join("; ", node.Meanings.Where(m => m.Planes.Any(p => node.Drop.Contains(p, StringComparer.OrdinalIgnoreCase)))
                    .Select(m => "there it is '" + m.Title + "'" + (m.Category.Length > 0 ? " [" + m.Category + "]" : string.Empty)));
                step.Details.Add(node.Relation + ": '" + node.Title + "' (" + node.Id + "): untick " + string.Join(", ", node.Drop) + (meanings.Length > 0 ? " - " + meanings : string.Empty));
            }
            if (found.Count(f => f.Drop.Count > 0) > 80)
                step.Details.Add("... and " + (found.Count(f => f.Drop.Count > 0) - 80).ToString(CultureInfo.InvariantCulture) + " more actions");
            if (otherKind > 0)
                step.Warnings.Add(otherKind.ToString(CultureInfo.InvariantCulture) + " actions are of the other kind than their relation (a button in an axis relation or the other way round): the relation's \"Looks wrong\" offers to move them.");
            if (sameFunction > 0)
                step.Warnings.Add(sameFunction.ToString(CultureInfo.InvariantCulture) + " aircraft have another title for the same function: they stay ticked.");
            if (dropped == 0)
            {
                step.Preview = true;
                step.Text = "Checked " + what + ": every action does the same in all its ticked aircraft";
                return string.Empty;
            }
            step.Text = "Check " + what + ": untick " + dropped.ToString(CultureInfo.InvariantCulture) + " aircraft where the action does something else (" +
                        found.Count(f => f.Drop.Count > 0).ToString(CultureInfo.InvariantCulture) + " actions in " +
                        found.Where(f => f.Drop.Count > 0).Select(f => f.Relation).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString(CultureInfo.InvariantCulture) + " relations)";
            step.Before = dropped.ToString(CultureInfo.InvariantCulture) + " aircraft ticked where the action does something else";
            step.After = "unticked";
            return string.Empty;
        }

        private static string Aircraft(Op op, EngineRelationPlanStep step)
        {
            string name = Find(op.Relation);
            if (name == null)
                return "Relation not found: " + op.Relation;
            if (op.Planes.Count == 0 || !op.On.HasValue)
                return "Name the aircraft and whether they are on or off.";
            Relation relation = InternalDataManagement.AllRelations[name];
            string before = Brief(relation);
            List<string> touched = new List<string>();
            foreach (RelationItem node in (relation.NODES ?? new List<RelationItem>()).Where(n => n != null && n.AIRCRAFT != null))
            {
                foreach (string plane in node.AIRCRAFT.Keys.ToList())
                {
                    if (!op.Planes.Any(p => PlaneMatches(plane, p)) || node.AIRCRAFT[plane] == op.On.Value)
                        continue;
                    node.AIRCRAFT[plane] = op.On.Value;
                    if (!touched.Contains(plane, StringComparer.OrdinalIgnoreCase))
                        touched.Add(plane);
                }
            }
            step.Relation = name;
            step.Text = (op.On.Value ? "Tick " : "Untick ") + string.Join(", ", op.Planes) + " in \"" + name + "\"";
            step.Before = before;
            step.After = Brief(relation);
            step.Details = touched;
            return touched.Count == 0 ? "No aircraft of \"" + name + "\" matches " + string.Join(", ", op.Planes) + " (or they are " + (op.On.Value ? "on" : "off") + " already)." : string.Empty;
        }

        internal static bool PlaneMatches(string plane, string wanted)
        {
            string a = Regex.Replace(plane ?? string.Empty, "[^A-Za-z0-9]", string.Empty);
            string b = Regex.Replace(wanted ?? string.Empty, "[^A-Za-z0-9]", string.Empty);
            return b.Length > 0 && (string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || (b.Length >= 3 && a.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private static string Groups(Op op, EngineRelationPlanStep step)
        {
            string name = Find(op.Relation);
            if (name == null)
                return "Relation not found: " + op.Relation;
            if (op.Add.Count == 0 && op.Remove.Count == 0)
                return "Say which groups to add or remove.";
            Relation relation = InternalDataManagement.AllRelations[name];
            if (relation.Groups == null)
                relation.Groups = new List<string>();
            string before = relation.Groups.Count == 0 ? "-" : string.Join(", ", relation.Groups);
            foreach (string group in op.Remove)
                relation.Groups.RemoveAll(g => string.Equals((g ?? string.Empty).Trim(), group.Trim(), StringComparison.OrdinalIgnoreCase));
            foreach (string group in op.Add)
            {
                string clean = Clean(group);
                if (clean.Length > 0 && !relation.Groups.Any(g => string.Equals((g ?? string.Empty).Trim(), clean, StringComparison.OrdinalIgnoreCase)))
                    relation.Groups.Add(clean);
            }
            step.Relation = name;
            step.Text = "Groups of \"" + name + "\"" + (op.Add.Count > 0 ? ": add " + string.Join(", ", op.Add) : string.Empty) + (op.Remove.Count > 0 ? (op.Add.Count > 0 ? ";" : ":") + " remove " + string.Join(", ", op.Remove) : string.Empty);
            step.Before = before;
            step.After = relation.Groups.Count == 0 ? "-" : string.Join(", ", relation.Groups);
            return before == step.After ? "The groups of \"" + name + "\" stay the same." : string.Empty;
        }

        private static string Tuning(Op op, EngineRelationPlanStep step)
        {
            string name = Find(op.Relation);
            if (name == null)
                return "Relation not found: " + op.Relation;
            Relation relation = InternalDataManagement.AllRelations[name];
            if (!relation.ISAXIS)
                return "\"" + name + "\" is a button relation; tuning is for axes.";
            Bind bind;
            if (!InternalDataManagement.AllBinds.TryGetValue(name, out bind) || bind == null)
                return "\"" + name + "\" has no bind yet; bind an axis first.";
            if (!op.Curvature.HasValue && !op.Deadzone.HasValue && !op.Saturation.HasValue && !op.Invert.HasValue)
                return "Say what to tune (curvature, deadzone, saturation, invert).";
            string before = TuningText(bind);
            if (op.Curvature.HasValue)
                bind.Curvature = new List<double> { Math.Max(-1, Math.Min(1, op.Curvature.Value)) };
            if (op.Deadzone.HasValue)
                bind.Deadzone = Math.Max(0, Math.Min(0.5, op.Deadzone.Value));
            if (op.Saturation.HasValue)
            {
                bind.SaturationX = Math.Max(0.1, Math.Min(1, op.Saturation.Value));
                bind.SaturationY = bind.SaturationX;
            }
            if (op.Invert.HasValue)
                bind.Inverted = op.Invert.Value;
            step.Relation = name;
            step.Text = "Tune the axis of \"" + name + "\"";
            step.Before = before;
            step.After = TuningText(bind);
            return before == step.After ? "The tuning stays the same." : string.Empty;
        }

        private static string TuningText(Bind bind)
        {
            double curve = bind.Curvature != null && bind.Curvature.Count > 0 ? bind.Curvature[0] : 0;
            return "curvature " + curve.ToString("0.##", CultureInfo.InvariantCulture) + ", deadzone " + bind.Deadzone.ToString("0.##", CultureInfo.InvariantCulture) +
                   ", saturation " + bind.SaturationX.ToString("0.##", CultureInfo.InvariantCulture) + ", " + (bind.Inverted == true ? "inverted" : "not inverted");
        }

        // The description through Group by description's offline rules: the catalog's nodes of one kind.
        private static string Resolve(string describe, string kind, out List<EngineNlGroupNode> nodes, out bool isAxis, out string function)
        {
            nodes = new List<EngineNlGroupNode>();
            isAxis = kind == "axis";
            function = string.Empty;
            EngineNlGroup.Options options = new EngineNlGroup.Options { Request = describe, DryRun = true, LlmMode = "off" };
            EngineNlGroupResult found = EngineNlGroup.Run(options);
            List<EngineNlGroupProposal> proposals = found.Proposals.Where(p => p.Nodes.Count > 0).ToList();
            if (kind == "axis" || kind == "button")
                proposals = proposals.Where(p => p.IsAxis == (kind == "axis")).ToList();
            if (proposals.Count == 0)
                return "Nothing in the catalog fits \"" + describe + "\"" + (found.Errors.Count > 0 ? " (" + string.Join(" ", found.Errors) + ")" : string.Empty) + ".";
            isAxis = proposals[0].IsAxis;
            bool axis = isAxis;
            function = proposals[0].Name;
            nodes = proposals.Where(p => p.IsAxis == axis).SelectMany(p => p.Nodes).Where(n => n.Spec.Length > 0).ToList();
            return nodes.Count == 0 ? "Nothing in the catalog fits \"" + describe + "\"." : string.Empty;
        }

        private static string NodeText(EngineNlGroupNode node)
        {
            string game = EngineRelationAssistantGameNames.Label(EngineRelationSuggest.GameKey(node.Game ?? string.Empty));
            return game + ": " + node.Id + (node.Planes.Count > 0 && game == "DCS" ? " (" + string.Join(", ", node.Planes.Take(6)) + (node.Planes.Count > 6 ? " ..." : string.Empty) + ")" : string.Empty);
        }

        private static string GameKeyOf(string game)
        {
            string key = EngineRelationSuggest.GameKey(game ?? string.Empty);
            foreach (string known in KnownGames())
            {
                if (string.Equals(EngineRelationAssistantGameNames.Label(known), game, StringComparison.OrdinalIgnoreCase) || string.Equals(known, game, StringComparison.OrdinalIgnoreCase))
                    return known;
            }
            return key;
        }

        private static IEnumerable<string> KnownGames()
        {
            return InternalDataManagement.AllRelations.Values.Where(r => r != null && r.NODES != null).SelectMany(r => r.NODES).Where(n => n != null)
                .Select(n => EngineRelationSuggest.GameKey(n.Game ?? string.Empty)).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        internal static string Brief(Relation relation)
        {
            List<RelationItem> nodes = (relation.NODES ?? new List<RelationItem>()).Where(n => n != null).ToList();
            int planes = nodes.Sum(n => n.AIRCRAFT == null ? 0 : n.AIRCRAFT.Count(p => p.Value));
            List<string> games = nodes.Select(n => EngineRelationAssistantGameNames.Label(EngineRelationSuggest.GameKey(n.Game ?? string.Empty))).Distinct().ToList();
            return nodes.Count.ToString(CultureInfo.InvariantCulture) + " actions, " + planes.ToString(CultureInfo.InvariantCulture) + " aircraft ticked" +
                   (games.Count > 0 ? " (" + string.Join(", ", games.Take(6)) + (games.Count > 6 ? " ..." : string.Empty) + ")" : string.Empty);
        }

        private static string Describe(Op op)
        {
            switch (op.Kind)
            {
                case "create": return "Create \"" + op.Name + "\" with " + op.Describe;
                case "delete": return "Delete " + string.Join(", ", op.Relations.Count > 0 ? op.Relations : new List<string> { op.Relation });
                case "rename": return "Rename \"" + op.Relation + "\" to \"" + op.To + "\"";
                case "add": return "Add " + op.Describe + " to \"" + op.Relation + "\"";
                case "remove": return "Remove " + (op.Describe.Length > 0 ? op.Describe : string.Join(", ", op.Games)) + " from \"" + op.Relation + "\"";
                case "aircraft": return (op.On == true ? "Tick " : "Untick ") + string.Join(", ", op.Planes) + " in \"" + op.Relation + "\"";
                case "groups": return "Groups of \"" + op.Relation + "\"";
                case "tuning": return "Tune the axis of \"" + op.Relation + "\"";
                case "fill": return "Fill " + string.Join(", ", op.Planes) + " into the relations where they fit";
                case "check": return "Check " + (op.Relations.Count > 0 ? string.Join(", ", op.Relations) : op.Relation.Length > 0 ? "\"" + op.Relation + "\"" : "every relation") + " on all its aircraft";
                default: return op.Kind;
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Asking: offline patterns first, then the local model
        // ------------------------------------------------------------------------------------------------------

        public static bool IsBound(string name)
        {
            Bind bind;
            return InternalDataManagement.AllBinds.TryGetValue(name, out bind) && bind != null && !string.IsNullOrWhiteSpace(bind.Joystick) &&
                   (!string.IsNullOrWhiteSpace(bind.JAxis) || !string.IsNullOrWhiteSpace(bind.JButton));
        }

        private static bool HasGame(Relation relation, string game)
        {
            return (relation.NODES ?? new List<RelationItem>()).Any(n => n != null &&
                (string.Equals(EngineRelationSuggest.GameKey(n.Game ?? string.Empty), game, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(EngineRelationAssistantGameNames.Label(EngineRelationSuggest.GameKey(n.Game ?? string.Empty)), game, StringComparison.OrdinalIgnoreCase)));
        }

        // Relations "that contain X": X is a game of the profile (its actions) or a part of the name.
        private static List<string> RelationsContaining(string what)
        {
            string wanted = Clean(what);
            string game = KnownGames().FirstOrDefault(g => string.Equals(g, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(EngineRelationAssistantGameNames.Label(g), wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Regex.Replace(EngineRelationAssistantGameNames.Label(g), @"\s+", string.Empty), Regex.Replace(wanted, @"\s+", string.Empty), StringComparison.OrdinalIgnoreCase));
            return InternalDataManagement.AllRelations
                .Where(r => r.Value != null && (game != null ? HasGame(r.Value, game) : r.Key.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(r => r.Key).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // A few requests need no model: append/prepend to names, delete relations without binds, rename A to B.
        internal static List<Op> Offline(string request, string scope, out string note)
        {
            note = string.Empty;
            string text = (request ?? string.Empty).Trim().TrimEnd('.', '!');
            Match m = Regex.Match(text, @"^(?:please\s+)?(?:append|add)\s+(?:the\s+)?(?:suffix\s+)?[""']?(?<x>[^""'\s]+)[""']?\s+(?:to|at the end of)\s+(?:the\s+names?\s+of\s+)?(?:every|each|all)(?:\s+the)?\s+relations?(?:\s+(?:that|which)\s+(?:contains?|has|have|includes?)|\s+(?:containing|with|including))\s+(?<y>.+)$", RegexOptions.IgnoreCase);
            bool prefix = false;
            if (!m.Success)
            {
                m = Regex.Match(text, @"^(?:please\s+)?(?:prepend|add\s+(?:the\s+)?prefix)\s+[""']?(?<x>[^""'\s]+)[""']?\s+to\s+(?:the\s+names?\s+of\s+)?(?:every|each|all)(?:\s+the)?\s+relations?(?:\s+(?:that|which)\s+(?:contains?|has|have|includes?)|\s+(?:containing|with|including))\s+(?<y>.+)$", RegexOptions.IgnoreCase);
                prefix = m.Success;
            }
            if (m.Success && scope.Length == 0)
            {
                string part = m.Groups["x"].Value;
                List<string> names = RelationsContaining(m.Groups["y"].Value);
                note = "offline rule: " + (prefix ? "prefix" : "append") + " \"" + part + "\" (" + names.Count.ToString(CultureInfo.InvariantCulture) + " relations)";
                return names.Where(n => prefix ? !n.StartsWith(part, StringComparison.OrdinalIgnoreCase) : !n.EndsWith(part, StringComparison.OrdinalIgnoreCase))
                    .Select(n => new Op { Kind = "rename", Relation = n, To = prefix ? part + n : n + part }).ToList();
            }
            if (scope.Length == 0)
            {
                List<Op> fillCheck = FillAndCheck(text, out note);
                if (fillCheck != null)
                    return fillCheck;
            }
            if (scope.Length == 0 && Regex.IsMatch(text, @"^(?:please\s+)?(?:delete|remove)\s+(?:all\s+|every\s+|the\s+)?relations?\s+(?:that\s+(?:have|has)\s+no|with\s+no|without(?:\s+a)?|without\s+any)\s+binds?$", RegexOptions.IgnoreCase))
            {
                List<string> names = InternalDataManagement.AllRelations.Keys.Where(n => !IsBound(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                note = "offline rule: relations without a bind (" + names.Count.ToString(CultureInfo.InvariantCulture) + ")";
                return names.Count == 0 ? new List<Op>() : new List<Op> { new Op { Kind = "delete", Relations = names } };
            }
            m = Regex.Match(text, @"^(?:please\s+)?rename\s+(?:the\s+relation\s+)?[""']?(?<a>.+?)[""']?\s+(?:to|into)\s+[""']?(?<b>.+?)[""']?$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string a = m.Groups["a"].Value;
                if (scope.Length > 0 && Regex.IsMatch(a, @"^(?:it|this|this relation)$", RegexOptions.IgnoreCase))
                    a = scope;
                if (Find(a) != null)
                {
                    note = "offline rule: rename";
                    return new List<Op> { new Op { Kind = "rename", Relation = Find(a), To = Clean(m.Groups["b"].Value) } };
                }
            }
            return null;
        }

        private const string FillVerbs = @"\b(?:fill|filled|filling|complete|completed|part of|add|added|adding|expand|expanded|expanding|extend|extended|extending|update|updated|updating|include|included|including|bring|put|cover|covered)\b";
        private const string FillNouns = @"\b(?:relations?|modules?|aircraft|airplanes?|planes?|helicopters?|jets?|profile)\b";
        private const string CheckWords = @"\b(?:check|checks|checking|verify|validate|make sure|ensure|review|go through|inspect|audit)\b|\bful+fil+s?\b|\bwork(?:s|ing)?\s+(?:properly|correctly|right)\b";

        // The aircraft a request names ("... modules: Mirage F1EE, F-15E Strike Eagle, C-130J"), as fill takes them: the
        // aircraft ids JoyPro placed and the names with a number it could not place (fill reports those).
        internal static List<string> NamedAircraft(string text)
        {
            List<string> unresolved = new List<string>();
            List<string> targets = EngineRelationFill.FromText(text, unresolved);
            List<string> names = targets.Select(EngineRelationFill.PlaneOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count > 0)
                names.AddRange(unresolved.Where(u => Regex.IsMatch(u, @"\d") && u.Split(' ').Length <= 4));
            return names;
        }

        // "Check all my relations ... on all modules in DCS ... expand them on the new modules: F-4E, CH-47F": a check of
        // every relation (aircraft where an action does something else are unticked) and a fill of the named aircraft (or
        // of every DCS aircraft when the request says all of them and names none).
        internal static List<Op> FillAndCheck(string text, out string note)
        {
            note = string.Empty;
            if (Regex.IsMatch(text, @"^(?:please\s+)?(?:delete|remove|rename|append|prepend|untick|tick|create)\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(text, @"\bnew relation\b|\bmake (?:a )?relation\b", RegexOptions.IgnoreCase))
                return null;
            bool aboutRelations = Regex.IsMatch(text, @"\brelations?\b|\bprofile\b", RegexOptions.IgnoreCase);
            bool fillWords = Regex.IsMatch(text, FillVerbs, RegexOptions.IgnoreCase) && Regex.IsMatch(text, FillNouns, RegexOptions.IgnoreCase);
            bool check = aboutRelations && Regex.IsMatch(text, CheckWords, RegexOptions.IgnoreCase);
            List<string> named = NamedAircraft(text);
            bool allDcs = EngineRelationFill.MentionsAllDcs(text);
            List<Op> ops = new List<Op>();
            List<string> parts = new List<string>();
            if (check)
            {
                ops.Add(new Op { Kind = "check" });
                parts.Add("check every relation on all its aircraft");
            }
            if ((fillWords || check) && named.Count > 0)
            {
                Op fill = new Op { Kind = "fill", Planes = named };
                Match into = Regex.Match(text, @"\b(?:to|into)\s+(?:the\s+)?(?:relation\s+)?[""']?(?<r>[^""',.:;]+?)[""']?(?:\s+relation)?\s*$", RegexOptions.IgnoreCase);
                if (into.Success && Find(into.Groups["r"].Value) != null)
                    fill.Relations.Add(Find(into.Groups["r"].Value));
                ops.Add(fill);
                parts.Add("fill " + named.Count.ToString(CultureInfo.InvariantCulture) + " named aircraft into " + (fill.Relations.Count > 0 ? "\"" + fill.Relations[0] + "\"" : "the relations where they fit"));
            }
            else if ((fillWords || check) && allDcs)
            {
                ops.Add(new Op { Kind = "fill", Planes = new List<string> { "all DCS aircraft" } });
                parts.Add("fill every DCS aircraft into the relations where it fits");
            }
            if (ops.Count == 0)
                return null;
            note = "offline rule: " + string.Join(", then ", parts);
            return ops;
        }

        // The exact rules (rename, prefix, delete without binds) do what the words say; fill and check read a free
        // sentence, so the local model (when it is on) reads it too.
        private static bool ExactRule(List<Op> ops)
        {
            return ops.All(o => o.Kind == "rename" || o.Kind == "delete");
        }

        public static EngineRelationPlanAsk Ask(string request, string scope, string llmMode)
        {
            EngineRelationPlanAsk ask = new EngineRelationPlanAsk { Request = (request ?? string.Empty).Trim(), Scope = scope ?? string.Empty };
            if (ask.Request.Length == 0)
            {
                ask.Errors.Add("Say what to do, e.g. \"append _sc to every relation that contains Star Citizen\".");
                return ask;
            }
            string note;
            List<Op> ops = Offline(ask.Request, ask.Scope, out note);
            string mode = string.IsNullOrWhiteSpace(llmMode) ? "auto" : llmMode.Trim().ToLowerInvariant();
            ask.Llm.Mode = mode;
            FillWithModel = mode != "off";
            EngineRelationFill.CheckAll = mode == "on";
            if (ops != null && (mode == "off" || mode == "auto" && ExactRule(ops)))
            {
                ask.Method = note;
            }
            else if (mode == "off")
            {
                ask.Errors.Add("JoyPro has no offline rule for this request, and the local AI model is switched off in this request. Switch it on (Settings > Local AI) or say it more simply, " +
                               "e.g. \"fill the F-4E Phantom II and the CH-47F into my relations\" or \"check all my relations\".");
                return ask;
            }
            else
            {
                EngineLlm.ModelInfo model;
                string problem;
                if (!EngineLlm.TryGetUsableModel(out model, out problem))
                {
                    if (ops == null)
                    {
                        ask.Errors.Add("JoyPro has no offline rule for this request, and the local AI model cannot help: " + problem);
                        return ask;
                    }
                    ask.Method = note;
                    ask.Warnings.Add("The local AI model was not asked: " + problem);
                }
                else
                {
                    ops = AskModel(model, ask, ops, note);
                    if (ops == null)
                        return ask;
                }
            }
            ask.Ops = ops.Select(o => o.ToMap()).ToList();
            // Checked against a copy of nothing: the steps run in this process only, the profile is not saved.
            EngineRelationPlanResult check = Execute(ops, ask.Scope, null);
            ask.Steps = check.Steps;
            if (ops.Count == 0)
                ask.Warnings.Add("Nothing to do: no relation matches the request.");
            return ask;
        }

        private const string SystemPrompt =
            "You turn a JoyPro user's request into a PLAN of relation operations. A relation groups the same function of many games and aircraft " +
            "(e.g. \"Pitch\" with the pitch axis of every DCS aircraft and of Star Citizen); it can have a bind (a device input) and groups (folders).\n" +
            "Answer with JSON only: {\"ops\":[...],\"reply\":\"one short sentence\"}. Allowed ops:\n" +
            "{\"op\":\"create\",\"name\":\"<new name>\",\"kind\":\"axis|button\",\"describe\":\"<what it does and where, e.g. pitch on all games but no helicopters>\"}\n" +
            "{\"op\":\"delete\",\"relations\":[\"<exact names>\"]}\n" +
            "{\"op\":\"rename\",\"relation\":\"<exact name>\",\"to\":\"<new name>\"}\n" +
            "{\"op\":\"add\",\"relation\":\"<exact name>\",\"describe\":\"<what to add>\"}\n" +
            "{\"op\":\"remove\",\"relation\":\"<exact name>\",\"describe\":\"<what to remove>\"} or {\"op\":\"remove\",\"relation\":\"<exact name>\",\"games\":[\"<game>\"]}\n" +
            "{\"op\":\"aircraft\",\"relation\":\"<exact name>\",\"planes\":[\"<aircraft>\"],\"on\":true|false}\n" +
            "{\"op\":\"groups\",\"relation\":\"<exact name>\",\"add\":[\"<group>\"],\"remove\":[\"<group>\"]}\n" +
            "{\"op\":\"tuning\",\"relation\":\"<exact name>\",\"curvature\":-1..1,\"deadzone\":0..0.5,\"saturation\":0.1..1,\"invert\":true|false}\n" +
            "{\"op\":\"fill\",\"planes\":[\"<aircraft or game names as the user wrote them>\"]} fills those aircraft into every relation where they fit (add \"relations\":[...] to limit it); " +
            "\"planes\":[\"all DCS aircraft\"] fills every DCS aircraft\n" +
            "{\"op\":\"check\"} checks every relation on all its ticked aircraft and unticks aircraft where the action does something else (add \"relations\":[...] to limit it)\n" +
            "Rules: use only relation names from the PROFILE list, exactly as written; never invent action ids (\"describe\" says it in words, JoyPro finds the actions); " +
            "one op per change; only what the user asked. When the user names aircraft or modules to add, expand, extend, update or bring into the relations, ALWAYS answer with " +
            "a fill op of exactly those names (the profile list does not show which aircraft are missing: JoyPro finds that). When the user asks to check, verify or review " +
            "the relations, ALWAYS add a check op. An empty ops list only when nothing of the request can be done with these ops.";

        private static string Summary(EngineRelationPlanAsk ask, int budgetTokens)
        {
            StringBuilder sb = new StringBuilder();
            List<string> games = KnownGames().Select(g => EngineRelationAssistantGameNames.Label(g)).Distinct().OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
            List<string> groups = InternalDataManagement.AllRelations.Values.Where(r => r != null && r.Groups != null).SelectMany(r => r.Groups).Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
            sb.AppendLine("PROFILE: " + InternalDataManagement.AllRelations.Count.ToString(CultureInfo.InvariantCulture) + " relations; games: " + (games.Count > 0 ? string.Join(", ", games) : "-") +
                          "; groups: " + (groups.Count > 0 ? string.Join(", ", groups.Take(40)) : "-"));
            IEnumerable<KeyValuePair<string, Relation>> relations = InternalDataManagement.AllRelations.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase);
            if (ask.Scope.Length > 0)
                relations = relations.Where(r => r.Key == ask.Scope);
            int budgetChars = Math.Max(800, budgetTokens * 3);
            int shown = 0;
            foreach (KeyValuePair<string, Relation> pair in relations)
            {
                Relation r = pair.Value;
                if (r == null)
                    continue;
                StringBuilder line = new StringBuilder();
                line.Append(pair.Key).Append(" | ").Append(r.ISAXIS ? "axis" : "button").Append(" | ").Append(Brief(r)).Append(" | ").Append(IsBound(pair.Key) ? "bound" : "unbound");
                if (r.Groups != null && r.Groups.Count > 0)
                    line.Append(" | groups: ").Append(string.Join(", ", r.Groups));
                if (ask.Scope.Length > 0)
                {
                    List<string> planes = (r.NODES ?? new List<RelationItem>()).Where(n => n != null && n.AIRCRAFT != null).SelectMany(n => n.AIRCRAFT.Where(p => p.Value).Select(p => p.Key)).Distinct().Take(80).ToList();
                    if (planes.Count > 0)
                        line.Append(" | ticked: ").Append(string.Join(", ", planes));
                    Bind bind;
                    if (r.ISAXIS && InternalDataManagement.AllBinds.TryGetValue(pair.Key, out bind) && bind != null)
                        line.Append(" | tuning: ").Append(TuningText(bind));
                }
                if (sb.Length + line.Length > budgetChars)
                {
                    sb.AppendLine("... and more relations");
                    break;
                }
                sb.AppendLine(line.ToString());
                shown++;
            }
            if (ask.Scope.Length > 0)
                sb.AppendLine("The user has \"" + ask.Scope + "\" open: change ONLY this relation (no create, no delete).");
            return sb.ToString();
        }

        internal static string Grammar()
        {
            return string.Join("\n", new[]
            {
                "root ::= \"{\" ws \"\\\"ops\\\"\" ws \":\" ws \"[\" ws ( op ( ws \",\" ws op )* )? ws \"]\" ws \",\" ws \"\\\"reply\\\"\" ws \":\" ws str ws \"}\"",
                "op ::= \"{\" ws \"\\\"op\\\"\" ws \":\" ws opname ( ws \",\" ws pair )* ws \"}\"",
                "opname ::= \"\\\"create\\\"\" | \"\\\"delete\\\"\" | \"\\\"rename\\\"\" | \"\\\"add\\\"\" | \"\\\"remove\\\"\" | \"\\\"aircraft\\\"\" | \"\\\"groups\\\"\" | \"\\\"tuning\\\"\" | \"\\\"fill\\\"\" | \"\\\"check\\\"\"",
                "pair ::= key ws \":\" ws value",
                "key ::= \"\\\"\" ( \"name\" | \"kind\" | \"describe\" | \"relation\" | \"to\" | \"relations\" | \"planes\" | \"games\" | \"on\" | \"add\" | \"remove\" | \"curvature\" | \"deadzone\" | \"saturation\" | \"invert\" ) \"\\\"\"",
                "value ::= str | \"[\" ws ( str ( ws \",\" ws str )* )? ws \"]\" | \"true\" | \"false\" | num",
                "str ::= \"\\\"\" ( [^\"\\\\\\x00-\\x1f] | \"\\\\\" [\"\\\\/nt] )* \"\\\"\"",
                "num ::= \"-\"? [0-9] [0-9]? ( \".\" [0-9] [0-9]? )?",
                "ws ::= [ \\n]?"
            });
        }

        // The context the plan asks for (also what "engine llm start" loads, so a started model serves the next plan).
        internal static int PlanContextSize(EngineLlm.ModelInfo model)
        {
            return model.Local ? Math.Max(model.ContextSize, 16384) : model.ContextSize;
        }

        internal static string ModelName(EngineLlm.ModelInfo model)
        {
            return string.IsNullOrWhiteSpace(model.Label) ? model.Id : model.Label;
        }

        // The model reads the request. JoyPro's own reading (fill, check) goes along as a hint and is the fallback when
        // the model does not start, does not answer, or answers with no steps for a request that names aircraft.
        private static List<Op> AskModel(EngineLlm.ModelInfo model, EngineRelationPlanAsk ask, List<Op> offline, string offlineNote)
        {
            int contextSize = PlanContextSize(model);
            int answerTokens = 1200;
            List<EngineLlm.Message> messages = new List<EngineLlm.Message>
            {
                new EngineLlm.Message { Role = "system", Content = SystemPrompt },
                new EngineLlm.Message { Role = "user", Content = "PROFILE:\nThrottle | axis | 30 actions, 40 aircraft ticked (DCS, Star Citizen) | bound\nGear | button | 12 actions, 20 aircraft ticked (DCS) | unbound\nREQUEST: delete Gear and make a new relation for the collective of all helicopters" },
                new EngineLlm.Message { Role = "assistant", Content = "{\"ops\":[{\"op\":\"delete\",\"relations\":[\"Gear\"]},{\"op\":\"create\",\"name\":\"Collective\",\"kind\":\"axis\",\"describe\":\"collective of all helicopters\"}],\"reply\":\"Deletes Gear and creates Collective.\"}" },
                new EngineLlm.Message { Role = "user", Content = "PROFILE:\nThrottle | axis | 30 actions, 40 aircraft ticked (DCS) | bound\nGear | button | 12 actions, 20 aircraft ticked (DCS) | bound\nREQUEST: my profile is old, please go through every relation, check that it works and expand it on the new modules F-4E Phantom II and CH-47F Chinook" },
                new EngineLlm.Message { Role = "assistant", Content = "{\"ops\":[{\"op\":\"check\"},{\"op\":\"fill\",\"planes\":[\"F-4E Phantom II\",\"CH-47F Chinook\"]}],\"reply\":\"Checks every relation and fills the F-4E and the CH-47F where they fit.\"}" }
            };
            int budget = contextSize - answerTokens - EngineLlm.EstimateTokens(messages) - 300;
            string hint = offline != null && offline.Count > 0
                ? "JOYPRO'S RULES READ IT AS: " + string.Join(" ", offline.Select(o => EngineSerialization.ToJson(o.ToMap()))) + " (keep these unless the request says otherwise)\n"
                : string.Empty;
            messages.Add(new EngineLlm.Message { Role = "user", Content = Summary(ask, budget) + hint + "REQUEST: " + ask.Request });
            ask.Method = "local AI model (" + ModelName(model) + ")";
            ask.Llm.Used = true;
            ask.Llm.Model = model.Id;
            EngineProgress.Step(0, 2, "Starting the local AI model", ModelName(model));
            EngineLlm.Session session = EngineLlm.Session.Start(model, contextSize);
            try
            {
                ask.Llm.ProcessId = session.ProcessId;
                if (session.Error.Length > 0)
                    return ModelFailed(ask, offline, offlineNote, "The local AI model (" + ModelName(model) + ") did not start: " + session.Error);
                EngineProgress.Step(1, 2, "The local AI model reads your request", ModelName(model));
                EngineLlm.CallResult call = session.Ask(messages, Grammar(), answerTokens, false);
                ask.Llm.Answer = call.Content ?? string.Empty;
                ask.Llm.DurationMs = call.DurationMs;
                if (!string.IsNullOrEmpty(call.Error))
                    return ModelFailed(ask, offline, offlineNote, "The local AI model (" + ModelName(model) + ") did not answer: " + call.Error);
                List<string> errors = new List<string>();
                List<Op> ops = Parse(call.Content, errors);
                Dictionary<string, object> answer = null;
                try { answer = EngineSerialization.FromJson<Dictionary<string, object>>(call.Content ?? string.Empty); } catch { }
                ask.Reply = answer == null ? string.Empty : EngineNlGroup.Text(answer, "reply");
                if (errors.Count > 0)
                    return ModelFailed(ask, offline, offlineNote, "The local AI model's answer was no plan JoyPro can read: " + string.Join("; ", errors));
                if (ops.Count == 0)
                {
                    List<Op> fallback = offline;
                    string why = offlineNote;
                    if (fallback == null || fallback.Count == 0)
                    {
                        List<string> named = NamedAircraft(ask.Request);
                        if (named.Count > 0)
                        {
                            fallback = new List<Op> { new Op { Kind = "fill", Planes = named } };
                            why = "fill the " + named.Count.ToString(CultureInfo.InvariantCulture) + " aircraft the request names";
                        }
                    }
                    if (fallback != null && fallback.Count > 0)
                    {
                        ask.Warnings.Add("The local AI model answered with no steps" + (ask.Reply.Length > 0 ? " (\"" + ask.Reply + "\")" : string.Empty) +
                                         "; JoyPro made the plan from the request itself: " + why.Replace("offline rule: ", string.Empty) + ".");
                        ask.Method = "local AI model (" + ModelName(model) + ") and JoyPro's rules";
                        return fallback;
                    }
                    return ops;
                }
                // What JoyPro's rules read from the request stays in the plan (the model may add more).
                if (offline != null)
                {
                    foreach (Op rule in offline.Where(o => o.Kind == "check" || o.Kind == "fill"))
                    {
                        if (!ops.Any(o => o.Kind == rule.Kind))
                        {
                            ops.Insert(rule.Kind == "check" ? 0 : ops.Count, rule);
                            ask.Method = "local AI model (" + ModelName(model) + ") and JoyPro's rules";
                        }
                    }
                }
                return ops;
            }
            finally
            {
                session.Dispose();
                ask.Llm.ProcessExited = session.ProcessExited;
            }
        }

        // The model could not make the plan: JoyPro's own reading when there is one (and why), else the reason.
        private static List<Op> ModelFailed(EngineRelationPlanAsk ask, List<Op> offline, string offlineNote, string reason)
        {
            if (offline != null && offline.Count > 0)
            {
                ask.Warnings.Add(reason + " JoyPro made the plan from the request itself.");
                ask.Method = offlineNote;
                return offline;
            }
            ask.Errors.Add(reason);
            return null;
        }

        public static string ToText(EngineRelationPlanAsk ask)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Plan for: " + ask.Request + (ask.Method.Length > 0 ? " (" + ask.Method + ")" : string.Empty));
            if (ask.Reply.Length > 0)
                sb.AppendLine(ask.Reply);
            AppendSteps(sb, ask.Steps);
            foreach (string warning in ask.Warnings)
                sb.AppendLine("Warning: " + warning);
            return sb.ToString().TrimEnd();
        }

        public static string ToText(EngineRelationPlanResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine((result.DryRun ? "Checked " : "Applied ") + result.Steps.Count(s => s.Ok).ToString(CultureInfo.InvariantCulture) + " of " + result.Steps.Count(s => !s.Skipped).ToString(CultureInfo.InvariantCulture) + " steps.");
            AppendSteps(sb, result.Steps);
            return sb.ToString().TrimEnd();
        }

        private static void AppendSteps(StringBuilder sb, List<EngineRelationPlanStep> steps)
        {
            foreach (EngineRelationPlanStep step in steps)
            {
                sb.Append(step.Index + 1).Append(". ").Append(step.Skipped ? "[skipped] " : step.Ok ? "[ok] " : "[rejected] ").Append(step.Text);
                if (step.Before.Length > 0 || step.After.Length > 0)
                    sb.Append(": ").Append(step.Before).Append(" -> ").Append(step.After);
                if (step.Error.Length > 0)
                    sb.Append(" - ").Append(step.Error);
                sb.AppendLine();
            }
        }
    }

    public sealed class EngineRelationPlanStep
    {
        public int Index { get; set; }
        public string Op { get; set; }
        public string Text { get; set; }
        public bool Ok { get; set; }
        public bool Skipped { get; set; }
        public string Error { get; set; }
        public string Relation { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
        public List<string> Details { get; set; }
        public List<string> Warnings { get; set; }
        public Dictionary<string, object> Plan { get; set; }
        // fill: the per-relation proposal (the app turns the ticked actions into add steps).
        public EngineFillResult Fill { get; set; }
        public bool Preview { get; set; }

        public EngineRelationPlanStep()
        {
            Op = string.Empty;
            Text = string.Empty;
            Error = string.Empty;
            Relation = string.Empty;
            Before = string.Empty;
            After = string.Empty;
            Details = new List<string>();
            Warnings = new List<string>();
            Plan = new Dictionary<string, object>();
        }
    }

    public sealed class EngineRelationPlanResult
    {
        public bool DryRun { get; set; }
        public bool Changed { get; set; }
        public string Scope { get; set; }
        public List<EngineRelationPlanStep> Steps { get; set; }
        public List<string> Errors { get; set; }

        public EngineRelationPlanResult()
        {
            Scope = string.Empty;
            Steps = new List<EngineRelationPlanStep>();
            Errors = new List<string>();
        }
    }

    public sealed class EngineRelationPlanAsk
    {
        public string Request { get; set; }
        public string Scope { get; set; }
        public string Method { get; set; }
        public string Reply { get; set; }
        public List<Dictionary<string, object>> Ops { get; set; }
        public List<EngineRelationPlanStep> Steps { get; set; }
        public EngineNlLlmInfo Llm { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public EngineRelationPlanAsk()
        {
            Request = string.Empty;
            Scope = string.Empty;
            Method = string.Empty;
            Reply = string.Empty;
            Ops = new List<Dictionary<string, object>>();
            Steps = new List<EngineRelationPlanStep>();
            Llm = new EngineNlLlmInfo();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }

    internal static partial class Program
    {
        // relations plan ask "<request>" [--relation <name>] [--llm auto|on|off]
        // relations plan apply (--file <plan.json> | <json>) [--relation <name>] [--only 0,2,3] [--dry-run]
        private static int RelationPlanCommand(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            string verb = args.Count > 0 ? args[0].ToLowerInvariant() : string.Empty;
            if (args.Count > 0)
                args.RemoveAt(0);
            string scope = PopOptionValue(args, "--relation") ?? string.Empty;
            if (scope.Length > 0)
            {
                string found = EngineRelationPlan.Find(scope);
                if (found == null)
                    return Fail("Relation not found: " + scope);
                scope = found;
            }
            if (verb == "ask")
            {
                string llm = (PopOptionValue(args, "--llm") ?? string.Empty).Trim().ToLowerInvariant();
                if (llm.Length > 0 && !EngineNlGroup.LlmModes.Contains(llm))
                    return Fail("Invalid --llm value: " + llm + ". Use " + string.Join(", ", EngineNlGroup.LlmModes) + ".");
                List<string> unknown = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
                if (unknown.Count > 0)
                    return Fail("Unknown relations plan ask argument(s): " + string.Join(" ", unknown));
                EngineRelationPlanAsk ask = EngineRelationPlan.Ask(string.Join(" ", args), scope, llm);
                if (ask.Errors.Count > 0 && ask.Steps.Count == 0)
                    return Fail(string.Join(Environment.NewLine, ask.Errors));
                EmitPayload(textOutput ? EngineRelationPlan.ToText(ask) : EngineSerialization.ToJson(ask), outPath);
                return 0;
            }
            if (verb == "apply")
            {
                bool dryRun = PopFlag(args, "--dry-run");
                string file = PopOptionValue(args, "--file");
                string onlyRaw = PopOptionValue(args, "--only");
                List<string> unknown = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
                if (unknown.Count > 0)
                    return Fail("Unknown relations plan apply argument(s): " + string.Join(" ", unknown));
                string json = file != null ? File.ReadAllText(NormalizePath(file), Encoding.UTF8) : string.Join(" ", args);
                List<string> errors = new List<string>();
                List<EngineRelationPlan.Op> ops = EngineRelationPlan.Parse(json, errors);
                if (errors.Count > 0)
                    return Fail(string.Join(Environment.NewLine, errors));
                if (ops.Count == 0)
                    return Fail("The plan has no steps.");
                HashSet<int> only = null;
                if (onlyRaw != null)
                {
                    only = new HashSet<int>();
                    foreach (string part in onlyRaw.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int index;
                        if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0)
                            return Fail("--only takes step numbers from 0, e.g. --only 0,2.");
                        only.Add(index);
                    }
                }
                EngineRelationPlanResult result = EngineRelationPlan.Execute(ops, scope, only);
                result.DryRun = dryRun;
                if (!dryRun && result.Changed)
                {
                    InternalDataManagement.ResyncBindsToMods();
                    InternalDataManagement.ResyncRelations();
                    InternalDataManagement.RecalcFigures();
                }
                EmitPayload(textOutput ? EngineRelationPlan.ToText(result) : EngineSerialization.ToJson(result), outPath);
                return 0;
            }
            return Fail("relations plan ask \"<request>\" [--relation <name>] [--llm auto|on|off] | relations plan apply (--file <plan.json> | <json>) [--relation <name>] [--only 0,2] [--dry-run]");
        }
    }
}
