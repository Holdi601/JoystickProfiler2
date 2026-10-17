#if NET
#nullable disable
#endif
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JoyPro.DeviceReference;

namespace JoyPro.Setup
{
    // The plan file (engine setup plan --json / --out, engine setup apply --plan): the same reader and writer in the
    // engine and the GUI (the review step edits the plan and hands it back to the engine).
    public static class SetupPlanJson
    {
        public static string Write(SetupPlan plan, bool indented)
        {
            Dictionary<string, object> root = new Dictionary<string, object>
            {
                { "Version", plan.Version },
                { "Games", plan.Games.Cast<object>().ToList() },
                { "Priorities", plan.Priorities.Select(t => (object)t.Cast<object>().ToList()).ToList() },
                { "ModifiersWanted", plan.ModifiersWanted },
                { "PreferFewer", plan.PreferFewer },
                { "Realism", plan.Realism },
                { "Seed", plan.Seed },
                { "CockpitSource", plan.CockpitSource },
                { "Devices", plan.Devices.Select(DeviceObject).Cast<object>().ToList() },
                { "Rules", plan.Rules.Cast<object>().ToList() },
                { "Modifiers", plan.Modifiers.Select(m => (object)new Dictionary<string, object>
                    {
                        { "Name", m.Name }, { "Device", m.Device }, { "Input", m.Input }, { "Control", m.Control }, { "Existing", m.Existing }, { "Forced", m.Forced }, { "Reason", m.Reason }
                    }).ToList() },
                { "Relations", plan.Relations.Select(RelationObject).Cast<object>().ToList() },
                { "NotPlaced", plan.NotPlaced.Select(n => (object)new Dictionary<string, object>
                    {
                        { "Relation", n.Relation }, { "Function", n.Function }, { "Category", n.Category }, { "Importance", n.Importance },
                        { "Aircraft", n.Aircraft.Cast<object>().ToList() }, { "Reason", n.Reason }
                    }).ToList() },
                { "Quality", plan.Quality.Select(q => (object)new Dictionary<string, object>
                    {
                        { "Aircraft", q.Aircraft }, { "Game", q.Game }, { "Tier", q.Tier }, { "Weight", q.Weight }, { "Score", q.Score }, { "Placed", q.Placed },
                        { "Total", q.Total }, { "EssentialPlaced", q.EssentialPlaced }, { "EssentialTotal", q.EssentialTotal }
                    }).ToList() },
                { "Notes", plan.Notes.Cast<object>().ToList() },
                { "Warnings", plan.Warnings.Cast<object>().ToList() },
                { "Inputs", plan.Inputs.Select(i => (object)new Dictionary<string, object>
                    {
                        { "Device", i.Device }, { "Input", i.Input }, { "Control", i.Control }, { "Label", i.Label }, { "Type", i.Type }, { "IsAxis", i.IsAxis }
                    }).ToList() }
            };
            return MiniJson.Write(root, indented);
        }

        public static Dictionary<string, object> DeviceObject(SetupDevice d)
        {
            return new Dictionary<string, object>
            {
                { "Device", d.Device }, { "Model", d.Model }, { "ModelId", d.ModelId }, { "Kind", d.Kind }, { "Hand", d.Hand }, { "Connected", d.Connected },
                { "Generic", d.Generic }, { "ForceFeedback", d.ForceFeedback }, { "NeedsGrip", d.NeedsGrip }, { "VidPid", d.VidPid }, { "Buttons", d.Buttons },
                { "Hats", d.Hats }, { "Axes", d.Axes }, { "ProfileUses", d.ProfileUses }, { "Role", d.Role }, { "ProposedRole", d.ProposedRole }, { "Reason", d.Reason },
                { "Grip", d.Grip },
                { "Grips", d.Grips.Select(g => (object)new Dictionary<string, object> { { "Id", g.Id }, { "Name", g.Name }, { "Image", g.Image } }).ToList() }
            };
        }

        private static Dictionary<string, object> RelationObject(SetupPlanRelation r)
        {
            return new Dictionary<string, object>
            {
                { "Name", r.Name }, { "Function", r.Function }, { "FunctionName", r.FunctionName }, { "Category", r.Category }, { "IsAxis", r.IsAxis },
                { "Existing", r.Existing }, { "Kept", r.Kept }, { "Removed", r.Removed },
                { "Nodes", r.Nodes.Select(n => (object)new Dictionary<string, object> { { "Game", n.Game }, { "Action", n.Action }, { "Aircraft", n.Aircraft } }).ToList() },
                { "Aircraft", r.Aircraft.Cast<object>().ToList() }, { "Overlap", r.Overlap.Cast<object>().ToList() }, { "Importance", r.Importance },
                { "Device", r.Device }, { "Input", r.Input }, { "Control", r.Control }, { "Label", r.Label }, { "Modifier", r.Modifier }, { "Score", r.Score },
                { "RealMatch", r.RealMatch }, { "Reason", r.Reason }
            };
        }

        public static SetupPlan Read(string json)
        {
            IDictionary<string, object> root = MiniJson.Parse(json ?? string.Empty) as IDictionary<string, object>;
            if (root == null)
                throw new FormatException("not a Setup wizard plan (JSON object expected)");
            // The engine's output may wrap the plan ({ "Plan": {...} }).
            IDictionary<string, object> wrapped = Get(root, "Plan") as IDictionary<string, object>;
            if (wrapped != null)
                root = wrapped;
            if (Get(root, "Relations") == null)
                throw new FormatException("not a Setup wizard plan (no Relations)");
            SetupPlan plan = new SetupPlan
            {
                Version = (int)Number(root, "Version", 1),
                Games = Strings(root, "Games"),
                Priorities = List(root, "Priorities").Select(t => t is IEnumerable && !(t is string) ? ((IEnumerable)t).Cast<object>().Select(Str).ToList() : new List<string> { Str(t) }).ToList(),
                ModifiersWanted = (int)Number(root, "ModifiersWanted", 0),
                PreferFewer = Bool(root, "PreferFewer"),
                Realism = Number(root, "Realism", 0.5),
                Seed = (int)Number(root, "Seed", 1),
                CockpitSource = Text(root, "CockpitSource"),
                Rules = Strings(root, "Rules"),
                Notes = Strings(root, "Notes"),
                Warnings = Strings(root, "Warnings")
            };
            foreach (IDictionary<string, object> d in Objects(root, "Devices"))
            {
                SetupDevice device = new SetupDevice
                {
                    Device = Text(d, "Device"), Model = Text(d, "Model"), ModelId = Text(d, "ModelId"), Kind = Text(d, "Kind"), Hand = Text(d, "Hand"),
                    Connected = Bool(d, "Connected"), Generic = Bool(d, "Generic"), ForceFeedback = Bool(d, "ForceFeedback"), NeedsGrip = Bool(d, "NeedsGrip"),
                    VidPid = Text(d, "VidPid"), Buttons = (int)Number(d, "Buttons", 0), Hats = (int)Number(d, "Hats", 0), Axes = (int)Number(d, "Axes", 0),
                    ProfileUses = (int)Number(d, "ProfileUses", 0), Role = Text(d, "Role"), ProposedRole = Text(d, "ProposedRole"), Reason = Text(d, "Reason"), Grip = Text(d, "Grip")
                };
                foreach (IDictionary<string, object> g in Objects(d, "Grips"))
                    device.Grips.Add(new SetupGripChoice { Id = Text(g, "Id"), Name = Text(g, "Name"), Image = Text(g, "Image") });
                plan.Devices.Add(device);
            }
            foreach (IDictionary<string, object> m in Objects(root, "Modifiers"))
            {
                plan.Modifiers.Add(new SetupPlanModifier
                {
                    Name = Text(m, "Name"), Device = Text(m, "Device"), Input = Text(m, "Input"), Control = Text(m, "Control"),
                    Existing = Bool(m, "Existing"), Forced = Bool(m, "Forced"), Reason = Text(m, "Reason")
                });
            }
            foreach (IDictionary<string, object> r in Objects(root, "Relations"))
            {
                SetupPlanRelation relation = new SetupPlanRelation
                {
                    Name = Text(r, "Name"), Function = Text(r, "Function"), FunctionName = Text(r, "FunctionName"), Category = Text(r, "Category"),
                    IsAxis = Bool(r, "IsAxis"), Existing = Bool(r, "Existing"), Kept = Bool(r, "Kept"), Removed = Bool(r, "Removed"),
                    Aircraft = Strings(r, "Aircraft"), Overlap = Strings(r, "Overlap"), Importance = Number(r, "Importance", 0),
                    Device = Text(r, "Device"), Input = Text(r, "Input"), Control = Text(r, "Control"), Label = Text(r, "Label"), Modifier = Text(r, "Modifier"),
                    Score = Number(r, "Score", 0), RealMatch = Number(r, "RealMatch", 0), Reason = Text(r, "Reason")
                };
                foreach (IDictionary<string, object> n in Objects(r, "Nodes"))
                    relation.Nodes.Add(new SetupNode { Game = Text(n, "Game"), Action = Text(n, "Action"), Aircraft = Text(n, "Aircraft") });
                plan.Relations.Add(relation);
            }
            foreach (IDictionary<string, object> n in Objects(root, "NotPlaced"))
            {
                plan.NotPlaced.Add(new SetupNotPlaced
                {
                    Relation = Text(n, "Relation"), Function = Text(n, "Function"), Category = Text(n, "Category"), Importance = Number(n, "Importance", 0),
                    Aircraft = Strings(n, "Aircraft"), Reason = Text(n, "Reason")
                });
            }
            foreach (IDictionary<string, object> q in Objects(root, "Quality"))
            {
                plan.Quality.Add(new SetupQuality
                {
                    Aircraft = Text(q, "Aircraft"), Game = Text(q, "Game"), Tier = (int)Number(q, "Tier", -1), Weight = Number(q, "Weight", 0), Score = Number(q, "Score", 0),
                    Placed = (int)Number(q, "Placed", 0), Total = (int)Number(q, "Total", 0), EssentialPlaced = (int)Number(q, "EssentialPlaced", 0),
                    EssentialTotal = (int)Number(q, "EssentialTotal", 0)
                });
            }
            foreach (IDictionary<string, object> i in Objects(root, "Inputs"))
            {
                plan.Inputs.Add(new SetupInput
                {
                    Device = Text(i, "Device"), Input = Text(i, "Input"), Control = Text(i, "Control"), Label = Text(i, "Label"),
                    Type = Text(i, "Type").Length > 0 ? Text(i, "Type") : "momentary", IsAxis = Bool(i, "IsAxis")
                });
            }
            return plan;
        }

        private static object Get(IDictionary<string, object> item, string key)
        {
            object value;
            if (item == null)
                return null;
            if (item.TryGetValue(key, out value))
                return value;
            foreach (KeyValuePair<string, object> pair in item)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }
            return null;
        }

        private static string Str(object value)
        {
            return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string Text(IDictionary<string, object> item, string key)
        {
            object value = Get(item, key);
            return value == null || value is IDictionary<string, object> ? string.Empty : Str(value);
        }

        private static bool Bool(IDictionary<string, object> item, string key)
        {
            object value = Get(item, key);
            return value is bool ? (bool)value : string.Equals(Str(value), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static double Number(IDictionary<string, object> item, string key, double fallback)
        {
            object value = Get(item, key);
            if (value == null)
                return fallback;
            double parsed;
            return double.TryParse(Str(value), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static List<object> List(IDictionary<string, object> item, string key)
        {
            object value = Get(item, key);
            return value is IEnumerable && !(value is string) && !(value is IDictionary<string, object>) ? ((IEnumerable)value).Cast<object>().ToList() : new List<object>();
        }

        private static List<string> Strings(IDictionary<string, object> item, string key)
        {
            return List(item, key).Select(Str).ToList();
        }

        private static IEnumerable<IDictionary<string, object>> Objects(IDictionary<string, object> item, string key)
        {
            return List(item, key).OfType<IDictionary<string, object>>();
        }
    }

    // Changes in the review step: move a function to another input, swap two, remove one, put it back. A change is
    // refused when the target input already serves a relation with an aircraft in common.
    public static class SetupPlanEdit
    {
        public static List<SetupPlanRelation> Conflicts(SetupPlan plan, SetupPlanRelation relation, string device, string input, string modifier, SetupPlanRelation except)
        {
            return plan.Relations.Where(r => r != relation && r != except && !r.Removed && r.Device == device &&
                                             string.Equals(r.Input, input, StringComparison.OrdinalIgnoreCase) &&
                                             string.Equals(r.Modifier ?? string.Empty, modifier ?? string.Empty, StringComparison.Ordinal) && Overlap(r, relation)).ToList();
        }

        public static bool Overlap(SetupPlanRelation a, SetupPlanRelation b)
        {
            List<string> x = a.Overlap.Count > 0 ? a.Overlap : a.Aircraft;
            List<string> y = b.Overlap.Count > 0 ? b.Overlap : b.Aircraft;
            if (x.Count == 0 || y.Count == 0 || x.Contains("*") || y.Contains("*"))
                return true;
            return x.Intersect(y, StringComparer.OrdinalIgnoreCase).Any();
        }

        // Inputs of the plan's devices a relation can move to (right type, free for its aircraft), with each layer.
        public static List<SetupInput> Targets(SetupPlan plan, SetupPlanRelation relation, out List<string> modifiers)
        {
            modifiers = new List<string> { string.Empty };
            modifiers.AddRange(plan.Modifiers.Select(m => m.Name));
            HashSet<string> modifierInputs = new HashSet<string>(plan.Modifiers.Select(m => m.Device + "|" + m.Input), StringComparer.OrdinalIgnoreCase);
            return plan.Inputs.Where(i => i.IsAxis == relation.IsAxis && !modifierInputs.Contains(i.Device + "|" + i.Input)).ToList();
        }

        public static string Move(SetupPlan plan, SetupPlanRelation relation, string device, string input, string modifier)
        {
            if (relation.Kept)
                return "\"" + relation.Name + "\" is your own bind; it stays.";
            SetupInput target = plan.Inputs.FirstOrDefault(i => i.Device == device && string.Equals(i.Input, input, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                return "That input is not part of the plan's devices.";
            if (target.IsAxis != relation.IsAxis)
                return relation.IsAxis ? "\"" + relation.Name + "\" needs an axis." : "\"" + relation.Name + "\" needs a button.";
            if (!string.IsNullOrEmpty(modifier) && plan.Modifiers.All(m => m.Name != modifier))
                return "Unknown modifier: " + modifier;
            if (plan.Modifiers.Any(m => m.Device == device && string.Equals(m.Input, input, StringComparison.OrdinalIgnoreCase)))
                return "That input is a modifier button.";
            List<SetupPlanRelation> conflicts = Conflicts(plan, relation, device, input, modifier, null);
            if (conflicts.Count > 0)
                return "Already used for the same aircraft by \"" + conflicts[0].Name + "\"; swap them instead.";
            relation.Device = device;
            relation.Input = target.Input;
            relation.Control = target.Control;
            relation.Label = target.Label;
            relation.Modifier = modifier ?? string.Empty;
            relation.Removed = false;
            relation.Reason = "moved by you";
            plan.NotPlaced.RemoveAll(n => n.Relation == relation.Name);
            return string.Empty;
        }

        public static string Swap(SetupPlan plan, SetupPlanRelation a, SetupPlanRelation b)
        {
            if (a.Kept || b.Kept)
                return "Your own binds stay; only the plan's new binds can be swapped.";
            if (a.IsAxis != b.IsAxis)
                return "An axis and a button cannot be swapped.";
            if (a.Device.Length == 0 || b.Device.Length == 0)
                return "Both functions need a place to swap; use Move for a function that is not placed.";
            if (Conflicts(plan, a, b.Device, b.Input, b.Modifier, b).Count > 0 || Conflicts(plan, b, a.Device, a.Input, a.Modifier, a).Count > 0)
                return "The swap would put a function on an input another relation of the same aircraft uses.";
            string device = a.Device, input = a.Input, control = a.Control, label = a.Label, modifier = a.Modifier;
            a.Device = b.Device; a.Input = b.Input; a.Control = b.Control; a.Label = b.Label; a.Modifier = b.Modifier; a.Reason = "swapped by you";
            b.Device = device; b.Input = input; b.Control = control; b.Label = label; b.Modifier = modifier; b.Reason = "swapped by you";
            return string.Empty;
        }

        public static string Remove(SetupPlan plan, SetupPlanRelation relation)
        {
            if (relation.Kept)
                return "\"" + relation.Name + "\" is your own bind; it stays.";
            relation.Removed = true;
            return string.Empty;
        }

        public static string Restore(SetupPlan plan, SetupPlanRelation relation)
        {
            if (!relation.Removed)
                return string.Empty;
            if (relation.Device.Length > 0 && Conflicts(plan, relation, relation.Device, relation.Input, relation.Modifier, null).Count > 0)
                return "Its input is used for the same aircraft now; move it instead.";
            relation.Removed = false;
            return string.Empty;
        }

        // Every input that serves two relations with an aircraft in common (apply refuses such a plan).
        public static List<string> Validate(SetupPlan plan)
        {
            List<string> problems = new List<string>();
            foreach (IGrouping<string, SetupPlanRelation> group in plan.Relations.Where(r => !r.Removed && r.Device.Length > 0)
                         .GroupBy(r => r.Device + "|" + r.Input.ToUpperInvariant() + "|" + r.Modifier, StringComparer.Ordinal))
            {
                List<SetupPlanRelation> list = group.ToList();
                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        if ((list[i].Kept && list[j].Kept) || !Overlap(list[i], list[j]))
                            continue;
                        problems.Add("\"" + list[i].Name + "\" and \"" + list[j].Name + "\" share " + list[i].Device + " " + list[i].Input +
                                     (list[i].Modifier.Length > 0 ? " + " + list[i].Modifier : string.Empty) + " in the same aircraft.");
                    }
                }
            }
            return problems;
        }
    }
}
