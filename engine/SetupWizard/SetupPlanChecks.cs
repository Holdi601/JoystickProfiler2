#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.Setup
{
    // The rules every plan must keep, checked on the plan itself (engine setup check, the combination matrix in
    // docs/auto-assign.md#combination-matrix): no input serves two relations of one aircraft (also across the normal and the
    // phase layers of one input), at most two modifiers unless the user asked for more, trigger / weapon release / pinky
    // only for their own functions, MFD keys only MFD functions in OSB order, printed panel labels only the functions they
    // name, slew never on a TMS hat and TMS never on a slew stick, CMS forward = dispense with the program change on
    // left / right, one device per hand, phase layers only for functions of their phase, no modifier layer for a game
    // without modifier binds. Plus measures: essential functions placed, time-critical functions on the grips.
    public sealed class SetupCheck
    {
        public string Id = string.Empty;
        public string Title = string.Empty;
        public bool Passed = true;
        public List<string> Problems = new List<string>();
    }

    public sealed class SetupCheckReport
    {
        public List<SetupCheck> Checks = new List<SetupCheck>();
        public int Placed;
        public int NotPlaced;
        public int EssentialPlaced;
        public int EssentialTotal;
        public int Instant;
        public int InstantOnGrips;
        public int Modifiers;
        public int PhaseLayers;
        public int OnPhaseLayers;

        public bool Passed { get { return Checks.All(c => c.Passed); } }

        public double InstantOnGripsShare { get { return Instant == 0 ? 1 : InstantOnGrips / (double)Instant; } }
    }

    public static class SetupPlanChecks
    {
        private static readonly Regex Dedicated = new Regex(@"^stick\.trigger\.|^stick\.weapon_release$|\.pinky\.", RegexOptions.Compiled);
        private static readonly Regex Osb = new Regex(@"\.osb\.([TBLR]\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static SetupCheckReport Check(SetupPlan plan, Dictionary<string, SetupFunction> functions, Dictionary<string, string> modifierSupport, int modifiersAllowed)
        {
            SetupCheckReport report = new SetupCheckReport();
            functions = functions ?? new Dictionary<string, SetupFunction>(StringComparer.OrdinalIgnoreCase);
            modifierSupport = modifierSupport ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<SetupPlanRelation> placed = plan.Placed.Where(r => !r.Kept).ToList();
            Dictionary<string, SetupDevice> devices = plan.Devices.GroupBy(d => d.Device, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            Dictionary<string, SetupPlanModifier> modifiers = plan.Modifiers.GroupBy(m => m.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            Func<SetupPlanRelation, SetupFunction> functionOf = r =>
            {
                SetupFunction f;
                return r.Function.Length > 0 && functions.TryGetValue(r.Function, out f) ? f : null;
            };
            report.Placed = placed.Count;
            report.NotPlaced = plan.NotPlaced.Count;
            report.Modifiers = plan.Modifiers.Count(m => !m.Switch);
            report.PhaseLayers = plan.Modifiers.Count(m => m.Switch);
            report.OnPhaseLayers = placed.Count(r => r.Modifier.Length > 0 && modifiers.ContainsKey(r.Modifier) && modifiers[r.Modifier].Switch);

            // C1 no input serves two relations of one aircraft (SetupPlanEdit.Validate, phase siblings included)
            SetupCheck conflicts = Add(report, "C1", "No input serves two relations of one aircraft");
            conflicts.Problems.AddRange(SetupPlanEdit.Validate(plan));

            // C2 at most two shift modifiers unless the user asked for more, at most two mode switch positions
            SetupCheck budget = Add(report, "C2", "At most " + Math.Max(2, modifiersAllowed).ToString(CultureInfo.InvariantCulture) + " shift modifiers and two phase layers");
            int used = plan.Modifiers.Count(m => !m.Switch && placed.Any(r => r.Modifier == m.Name));
            if (used > Math.Max(2, modifiersAllowed))
                budget.Problems.Add(used.ToString(CultureInfo.InvariantCulture) + " shift modifiers in use: " + string.Join(", ", plan.Modifiers.Where(m => !m.Switch).Select(m => m.Name)));
            if (plan.Modifiers.Count(m => m.Switch) > 2)
                budget.Problems.Add(plan.Modifiers.Count(m => m.Switch).ToString(CultureInfo.InvariantCulture) + " phase layers");

            // C3 trigger, weapon release and pinky only for functions made for them
            SetupCheck dedicated = Add(report, "C3", "Trigger, weapon release and pinky only take their own functions");
            foreach (SetupPlanRelation r in placed.Where(r => Dedicated.IsMatch(RuleControl(r, devices))))
            {
                SetupFunction f = functionOf(r);
                string control = RuleControl(r, devices);
                if (f == null || !f.Prefs.Any(p => Matches(p, control)))
                    dedicated.Problems.Add(r.Name + " on " + r.Control);
            }

            // C4 MFD keys only MFD functions, every MFD function on its own OSB position
            SetupCheck mfd = Add(report, "C4", "MFD keys only take MFD functions, in OSB order");
            foreach (SetupPlanRelation r in placed)
            {
                SetupFunction f = functionOf(r);
                bool mfdFunction = f != null && SetupPhases.IsMfdFunction(f.Prefs);
                bool mfdInput = SetupPhases.IsMfdControl(r.Control) || (devices.ContainsKey(r.Device) && string.Equals(devices[r.Device].Kind, "mfd", StringComparison.OrdinalIgnoreCase));
                if (mfdInput && !mfdFunction)
                    mfd.Problems.Add(r.Name + " (not an MFD function) on " + r.Control);
                if (mfdFunction)
                {
                    Match wantedOsb = Osb.Match(f.Prefs[0]);
                    Match gotOsb = Osb.Match(r.Control);
                    if (!gotOsb.Success || (wantedOsb.Success && !string.Equals(wantedOsb.Groups[1].Value, gotOsb.Groups[1].Value, StringComparison.OrdinalIgnoreCase)))
                        mfd.Problems.Add(r.Name + " (" + f.Prefs[0] + ") on " + r.Control);
                }
            }

            // C5 printed panel labels only take the functions they name; a replica's unlabelled controls only their own
            SetupCheck labels = Add(report, "C5", "Printed panel labels only take the functions they name");
            Dictionary<string, bool> replica = plan.Inputs.GroupBy(i => i.Device, StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => devices.ContainsKey(g.Key) && SetupPhases.IsReplicaPanel(devices[g.Key].Kind, g.Where(i => !i.IsAxis).Select(i => i.Label)) &&
                     (devices[g.Key].Kind == "panel" || devices[g.Key].Kind == "buttonbox" || devices[g.Key].Kind == "mfd"), StringComparer.Ordinal);
            // (and a base's cockpit labels: the Warthog throttle's "LDG GEAR WARN SILENCE" is no place for the gear or master arm)
            foreach (SetupPlanRelation r in placed.Where(r => r.Control.StartsWith("panel.", StringComparison.Ordinal) ||
                                                              (r.Control.IndexOf(".base.", StringComparison.Ordinal) >= 0 && SetupPhases.IsCockpitLabel(r.Label))))
            {
                SetupFunction f = functionOf(r);
                bool exact = f != null && f.Prefs.Count > 0 && f.Prefs[0].IndexOf('*') < 0 && string.Equals(f.Prefs[0], r.Control, StringComparison.OrdinalIgnoreCase);
                bool named = SetupPhases.LabelNames(f, r.Label);
                bool printed = SetupPhases.IsFunctionLabel(r.Label);
                // A base's cockpit label may take another function once the unlabelled inputs are used up, but never one it
                // contradicts or a time-critical / weapon function.
                if (!r.Control.StartsWith("panel.", StringComparison.Ordinal))
                {
                    if (f != null && !exact && !named && r.Reason.IndexOf("your own control", StringComparison.Ordinal) < 0 &&
                        (SetupPhases.LabelContradicts(f.Labels, f.Id + " " + f.Name, r.Label) || SetupPhases.IsCriticalFunction(f.Id, f.Crit, r.Category)))
                        labels.Problems.Add(r.Name + " on \"" + r.Label + "\"");
                    continue;
                }
                if (printed && !exact && !named && r.Reason.IndexOf("your own control", StringComparison.Ordinal) < 0 && r.Reason.IndexOf("your preference", StringComparison.Ordinal) < 0)
                    labels.Problems.Add(r.Name + " on \"" + r.Label + "\"");
                else if (!printed && replica.ContainsKey(r.Device) && replica[r.Device] && !exact && r.Reason.IndexOf("your own control", StringComparison.Ordinal) < 0)
                    labels.Problems.Add(r.Name + " on the unlabelled " + r.Control + " of a cockpit replica");
            }

            // C6 slew never on a TMS hat, TMS never on a slew stick
            SetupCheck slew = Add(report, "C6", "Slew is not TMS (slew on slew controls, TMS on its hat)");
            foreach (SetupPlanRelation r in placed)
            {
                SetupFunction f = functionOf(r);
                string control = RuleControl(r, devices);
                if (f == null || f.Prefs.Any(p => Matches(p, control)) || r.Reason.IndexOf("your own control", StringComparison.Ordinal) >= 0 || r.Reason.IndexOf("your preference", StringComparison.Ordinal) >= 0)
                    continue;
                if (SetupPhases.SlewTmsClash(r.Function, f.Group, control))
                    slew.Problems.Add(r.Name + " on " + r.Control);
            }

            // C7 CMS forward = dispense, left / right = program change (on one hat)
            SetupCheck cms = Add(report, "C7", "CMS forward dispenses, left / right change the program");
            SetupPlanRelation forward = placed.FirstOrDefault(r => r.Function == "cms_fwd_start_program_or_just_flare");
            foreach (SetupPlanRelation side in placed.Where(r => r.Function == "cms_left" || r.Function == "cms_right"))
            {
                string want = side.Function == "cms_left" ? "left" : "right";
                if (forward != null && Prefix(forward.Control) == Prefix(side.Control) && Last(side.Control) != want)
                    cms.Problems.Add(side.Name + " on " + side.Control + " (CMS forward on " + forward.Control + ")");
            }
            if (forward != null && Regex.IsMatch(forward.Control, @"\.hat\.") && !Regex.IsMatch(forward.Control, @"\.(up|fwd)$"))
                cms.Problems.Add(forward.Name + " on " + forward.Control + " (not the forward position)");

            // C8 one device per hand
            SetupCheck hands = Add(report, "C8", "One device per hand");
            foreach (IGrouping<string, SetupDevice> role in plan.Devices.Where(d => d.Role == SetupRoles.MainStick || d.Role == SetupRoles.LeftStick || d.Role == SetupRoles.Throttle ||
                                                                                     d.Role == SetupRoles.Collective || d.Role == SetupRoles.Pedals || d.Role == SetupRoles.HeliStick).GroupBy(d => d.Role))
            {
                if (role.Count() > 1)
                    hands.Problems.Add(role.Key + ": " + string.Join(", ", role.Select(d => d.Model)));
            }
            HashSet<string> binding = new HashSet<string>(plan.Devices.Where(d => SetupRoles.Binds(d.Role)).Select(d => d.Device), StringComparer.Ordinal);
            foreach (SetupPlanRelation r in placed.Where(r => !binding.Contains(r.Device)))
                hands.Problems.Add(r.Name + " on " + r.Device + ", a device without a role that binds");

            // C9 phase layers only for functions of their phase; their modifiers are mode switch positions
            SetupCheck phase = Add(report, "C9", "Phase layers only take functions of their phase");
            foreach (SetupPlanRelation r in placed.Where(r => r.Modifier.Length > 0 && modifiers.ContainsKey(r.Modifier) && modifiers[r.Modifier].Switch))
            {
                SetupFunction f = functionOf(r);
                string exclusive = f == null ? string.Empty : SetupPhases.ExclusivePhase(f.Phases);
                if (exclusive != modifiers[r.Modifier].Phase)
                    phase.Problems.Add(r.Name + " (" + (f == null ? "no phase" : string.Join("|", f.Phases)) + ") on the " + modifiers[r.Modifier].Phase + " layer");
            }

            // C10 no modifier layer for a game without modifier binds (no phase layer without full support)
            SetupCheck games = Add(report, "C10", "Modifier layers only for games that handle them");
            foreach (SetupPlanRelation r in placed.Where(r => r.Modifier.Length > 0))
            {
                bool phaseLayer = modifiers.ContainsKey(r.Modifier) && modifiers[r.Modifier].Switch;
                foreach (string game in r.Nodes.Select(n => string.IsNullOrWhiteSpace(n.Game) ? "DCS" : n.Game).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string support;
                    if (modifierSupport.TryGetValue(game, out support) && !SetupPhases.AllowsLayer(support, phaseLayer))
                        games.Problems.Add(r.Name + " (" + game + ": modifiers " + support + ") on " + r.Modifier);
                }
            }

            // C11 a directional set (TMS, DMS, CMS, trim, slew ...) together on one hat's matching directions, or not at all
            SetupCheck sets = Add(report, "C11", "Directional sets together on one hat's matching directions");
            HashSet<string> directional = SetupPhases.DirectionalGroups(functions.Values.Select(f => f.Group));
            List<Tuple<SetupPlanRelation, string, string>> members = new List<Tuple<SetupPlanRelation, string, string>>();
            foreach (SetupPlanRelation r in placed.Where(r => r.Reason.IndexOf("your own control", StringComparison.Ordinal) < 0))
            {
                SetupFunction f = functionOf(r);
                string key, role;
                if (f == null || !SetupPhases.DirectionalMember(f.Group, directional, out key, out role))
                    continue;
                string control = RuleControl(r, devices);
                if (!SetupPhases.MatchesDirection(role, control))
                    sets.Problems.Add(r.Name + " (" + key.ToUpperInvariant() + " " + role + ") on " + r.Control);
                members.Add(Tuple.Create(r, key, r.Device + "|" + r.Modifier + "|" + SetupPhases.HatOf(control)));
            }
            foreach (IGrouping<string, Tuple<SetupPlanRelation, string, string>> group in members.GroupBy(m => m.Item2, StringComparer.Ordinal))
            {
                List<Tuple<SetupPlanRelation, string, string>> list = group.ToList();
                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        if (list[i].Item3 != list[j].Item3 && SetupPlanEdit.Overlap(list[i].Item1, list[j].Item1))
                            sets.Problems.Add(list[i].Item1.Name + " and " + list[j].Item1.Name + " (" + group.Key.ToUpperInvariant() + ") on different hats: " + list[i].Item1.Control + " / " + list[j].Item1.Control);
                    }
                }
            }

            // Measures
            SetupQuality top = plan.Quality.FirstOrDefault(q => q.Tier == 0) ?? plan.Quality.FirstOrDefault();
            if (top != null)
            {
                report.EssentialPlaced = top.EssentialPlaced;
                report.EssentialTotal = top.EssentialTotal;
            }
            foreach (SetupPlanRelation r in placed)
            {
                SetupFunction f = functionOf(r);
                if (f == null || f.Crit != SetupPhases.Instant || f.IsAxis)
                    continue;
                report.Instant++;
                if (IsGripControl(RuleControl(r, devices)))
                    report.InstantOnGrips++;
            }

            foreach (SetupCheck check in report.Checks)
                check.Passed = check.Problems.Count == 0;
            return report;
        }

        private static SetupCheck Add(SetupCheckReport report, string id, string title)
        {
            SetupCheck check = new SetupCheck { Id = id, Title = title };
            report.Checks.Add(check);
            return check;
        }

        // A left stick's controls count as the throttle side (SetupPlanner.LeftStickAlias).
        private static string RuleControl(SetupPlanRelation r, Dictionary<string, SetupDevice> devices)
        {
            SetupDevice device;
            if (devices.TryGetValue(r.Device, out device) && device.Role == SetupRoles.LeftStick && r.Control.StartsWith("stick.", StringComparison.Ordinal))
                return SetupPlanner.LeftStickAlias(r.Control);
            return r.Control;
        }

        private static bool IsGripControl(string control)
        {
            return SetupPlanner.IsGrip(control);
        }

        private static bool Matches(string pattern, string control)
        {
            return SetupPlanner.PatternMatch(null, pattern, control);
        }

        private static string Prefix(string control)
        {
            int dot = (control ?? string.Empty).LastIndexOf('.');
            return dot > 0 ? control.Substring(0, dot) : control ?? string.Empty;
        }

        private static string Last(string control)
        {
            int dot = (control ?? string.Empty).LastIndexOf('.');
            return dot >= 0 ? control.Substring(dot + 1) : control ?? string.Empty;
        }

        public static string ToText(SetupCheckReport report)
        {
            List<string> lines = new List<string>();
            lines.Add("Checks: " + (report.Passed ? "all passed" : report.Checks.Count(c => !c.Passed).ToString(CultureInfo.InvariantCulture) + " failed"));
            foreach (SetupCheck check in report.Checks)
            {
                lines.Add("  " + (check.Passed ? "PASS " : "FAIL ") + check.Id + " " + check.Title + (check.Passed ? string.Empty : " (" + check.Problems.Count.ToString(CultureInfo.InvariantCulture) + ")"));
                foreach (string problem in check.Problems.Take(8))
                    lines.Add("       - " + problem);
            }
            lines.Add("Placed: " + report.Placed.ToString(CultureInfo.InvariantCulture) + ", not placed: " + report.NotPlaced.ToString(CultureInfo.InvariantCulture) +
                      ", essential of the top aircraft: " + report.EssentialPlaced.ToString(CultureInfo.InvariantCulture) + "/" + report.EssentialTotal.ToString(CultureInfo.InvariantCulture) +
                      ", time-critical on the grips: " + report.InstantOnGrips.ToString(CultureInfo.InvariantCulture) + "/" + report.Instant.ToString(CultureInfo.InvariantCulture) +
                      ", modifiers: " + report.Modifiers.ToString(CultureInfo.InvariantCulture) + ", phase layers: " + report.PhaseLayers.ToString(CultureInfo.InvariantCulture) +
                      " (" + report.OnPhaseLayers.ToString(CultureInfo.InvariantCulture) + " functions)");
            return string.Join(Environment.NewLine, lines);
        }
    }
}
