using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Setup;

namespace JoyPro.Engine
{
    // The headline metric of engine setup eval (docs/auto-setup-eval.md#in-flight-essentials): per setup x game / aircraft,
    // which of the functions a pilot needs quickly in flight can be worked - and only counted when they are where a hand
    // is in flight (grip, throttle / collective / quadrant, or a printed lever / switch of their own), without a modifier
    // for the time-critical ones, on a fitting kind of input in the right direction, and not destructive.
    internal static partial class EngineSetupEval
    {
        internal sealed class EvalEssential
        {
            public string Id = string.Empty;
            public string Status = "ok";        // ok, fail, n/a
            public bool Critical;              // time-critical: grip / throttle without a modifier
            public string Detail = string.Empty;
        }

        // Game classes and their essentials, in the order that MUST fit first on a small device.
        internal static readonly string[] EssentialOrder =
            { "pitch", "roll", "throttle", "fire", "yaw", "wheel_brakes", "release", "countermeasures", "trim", "target", "look", "gear", "flaps", "airbrake", "ptt" };

        internal static readonly Dictionary<string, string[]> EssentialsOfClass = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "jet", new[] { "pitch", "roll", "throttle", "fire", "yaw", "wheel_brakes", "release", "countermeasures", "trim", "target", "look", "gear", "flaps", "airbrake", "ptt" } },
            { "prop", new[] { "pitch", "roll", "throttle", "fire", "yaw", "wheel_brakes", "release", "trim", "look", "gear", "flaps", "ptt" } },
            { "heli", new[] { "pitch", "roll", "throttle", "fire", "yaw", "wheel_brakes", "release", "countermeasures", "trim", "target", "look", "gear", "ptt" } },
            { "civil", new[] { "pitch", "roll", "throttle", "yaw", "wheel_brakes", "trim", "look", "gear", "flaps", "airbrake" } },
            { "space", new[] { "pitch", "roll", "throttle", "fire", "yaw", "release", "countermeasures", "target", "look" } },
            { "pad", new[] { "pitch", "roll", "throttle", "fire", "yaw", "release", "countermeasures", "target" } }
        };

        private static readonly HashSet<string> AxisEssentials = new HashSet<string>(StringComparer.Ordinal) { "pitch", "roll", "throttle", "yaw" };
        private static readonly HashSet<string> GateEssentials = new HashSet<string>(StringComparer.Ordinal) { "pitch", "roll", "throttle", "yaw", "fire", "release", "countermeasures", "wheel_brakes", "look" };
        private static readonly HashSet<string> CivilGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MSFS2024", "MSFS2020", "XPlane12", "XPlane11", "FSX", "FSXSE", "P3D4", "P3D5", "P3D6" };
        private static readonly HashSet<string> PropGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IL2Game", "IL21946", "IL2CoD", "IL2CoDBlitz" };
        private static readonly HashSet<string> PropPlanes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "P-51D", "P-51D-30-NA", "TF-51D", "SpitfireLFMkIX", "SpitfireLFMkIXCW", "Bf-109K-4", "FW-190A8", "FW-190D9", "P-47D-30", "P-47D-30bl1", "P-47D-40", "MosquitoFBMkVI", "I-16", "Yak-52", "Christen Eagle II" };

        internal static string EssentialClass(EvalTarget target, string cls)
        {
            if (target.Pad)
                return "pad";
            if (cls == SetupClasses.Space)
                return "space";
            if (CivilGames.Contains(target.Game))
                return "civil";
            if (cls == SetupClasses.Rotary)
                return "heli";
            if (PropGames.Contains(target.Game) || PropPlanes.Contains(target.Aircraft))
                return "prop";
            return "jet";
        }

        // Called by HardGates before small devices get their waiver: the gates' own verdicts for the axes and the gun /
        // release / flares / brakes / look; gear, flaps, trim, airbrake, target and PTT judged here.
        private static void Essentials(EvalRow row, EvalTarget target, SetupRequest request, SetupPlan plan, string cls, HashSet<string> available,
                                       Func<string, List<SetupPlanRelation>> of, Func<SetupPlanRelation, bool> clean, Func<SetupPlanRelation, bool> onAxis,
                                       Func<SetupPlanRelation, SetupDevice> deviceOf, Func<SetupPlanRelation, SetupInput> inputOf, Func<SetupPlanRelation, string> where)
        {
            string klass = EssentialClass(target, cls);
            row.EssentialClass = klass;
            row.Essentials.Clear();
            // (the gear may sit behind a modifier: a deliberate two-finger action is safer than a grip button one slip away)
            bool modifierOk = false;
            Func<SetupPlanRelation, bool> reach = r =>
            {
                if (!clean(r) && !modifierOk)
                    return false;
                SetupDevice d = deviceOf(r);
                string role = d == null ? string.Empty : d.Role;
                bool hand = role == SetupRoles.MainStick || role == SetupRoles.HeliStick || role == SetupRoles.Throttle || role == SetupRoles.Collective || role == SetupRoles.LeftStick;
                bool grip = hand && r.Control.IndexOf(".base.", StringComparison.Ordinal) < 0 && !r.Control.StartsWith("panel.", StringComparison.Ordinal);
                bool leftHand = role == SetupRoles.Throttle || role == SetupRoles.Collective;   // the throttle side incl. its base and levers
                SetupFunction f;
                bool labelled = request.Functions.TryGetValue(r.Function ?? string.Empty, out f) && SetupPhases.LabelNames(f, r.Label ?? string.Empty);
                return grip || leftHand || labelled || r.Control.StartsWith("panel.lever.", StringComparison.Ordinal);
            };
            Func<SetupPlanRelation, bool> sensible = r =>
            {
                SetupInput input = inputOf(r);
                SetupFunction f;
                request.Functions.TryGetValue(r.Function ?? string.Empty, out f);
                if (f != null && f.IsAxis != onAxis(r))
                    return false;
                if (input != null && !input.IsAxis)
                {
                    bool zone = string.Equals(input.Type, "detent", StringComparison.OrdinalIgnoreCase) && !DetentControl.IsMatch(input.Control);
                    if (zone || ((DetentControl.IsMatch(input.Control) || DetentLabel.IsMatch(input.Label ?? string.Empty)) && !ThrottleFamily.IsMatch(r.Function ?? string.Empty)))
                        return false;
                    if (string.Equals(input.Type, "latched", StringComparison.OrdinalIgnoreCase) && f != null && !SetupPlanner.LatchedFits(f, input))
                        return false;
                    if (SetupPlanner.OppositeHatDirection(f, input.Control))
                        return false;
                }
                // not one of the row's destructive bindings
                string at = where(r);
                return !row.Destructive.Any(x => x.EndsWith(at, StringComparison.Ordinal));
            };
            Func<string, bool> good = id => of(id).Any(r => reach(r) && sensible(r));
            Func<string[], bool> any = ids => ids.Any(available.Contains);

            foreach (string id in EssentialsOfClass[klass])
            {
                EvalEssential e = new EvalEssential { Id = id, Critical = !AxisEssentials.Contains(id) };
                if (target.Pad || GateEssentials.Contains(id))
                {
                    string gateId = target.Pad && id == "target" ? "pad target" : id;
                    EvalGate gate = row.Gates.LastOrDefault(g => g.Gate == gateId);
                    if (gate == null)
                    {
                        e.Status = "n/a";
                        e.Detail = "no gate (the game has no such command)";
                    }
                    else
                    {
                        e.Status = gate.Status;
                        e.Detail = gate.Detail;
                    }
                    row.Essentials.Add(e);
                    continue;
                }
                bool ok;
                bool applies;
                switch (id)
                {
                    case "gear":
                        applies = any(new[] { "gear_toggle", "gearin", "gearout" });
                        modifierOk = true;
                        ok = good("gear_toggle") || (good("gearin") && good("gearout"));
                        modifierOk = false;
                        break;
                    case "flaps":
                        applies = any(new[] { "flaps_toggle", "flapsup", "flapsdown", "flaps_axis", "flaps_notch_up", "flaps_notch_down" });
                        ok = good("flaps_toggle") || (good("flapsup") && good("flapsdown")) || good("flaps_axis") || (good("flaps_notch_up") && good("flaps_notch_down"));
                        break;
                    case "trim":
                        // (a helicopter with one trimmer button - the Ka-50's force trim - has no nose up / down to ask for)
                        applies = any(new[] { "trimnoseup", "trim_nosedown", "trim_axis_elevator", "trimmerset" });
                        ok = (good("trimnoseup") && good("trim_nosedown")) || good("trim_axis_elevator") ||
                             // (the Ka-50 III's one TRIMMER button is catalogued as its trimmer set and its nose-down trim)
                             (!available.Contains("trimnoseup") && available.Contains("trimmerset") && (good("trimmerset") || good("trim_nosedown")));
                        break;
                    case "airbrake":
                        applies = any(new[] { "airbrake_toggle", "airbakeextend", "airbrakeretract", "airbrake_axis" });
                        ok = good("airbrake_toggle") || (good("airbakeextend") && good("airbrakeretract")) || good("airbrake_axis");
                        break;
                    case "target":
                        applies = any(new[] { "target_management_system_up", "radar_lock", "space_target_closest_hostile", "space_target_hostile_next" });
                        ok = good("target_management_system_up") || good("radar_lock") || good("space_target_closest_hostile") || good("space_target_hostile_next");
                        break;
                    case "ptt":
                        applies = any(new[] { "comm1_up", "easycomms" });
                        ok = good("comm1_up") || good("easycomms");
                        break;
                    default:
                        applies = false;
                        ok = false;
                        break;
                }
                e.Status = !applies ? "n/a" : ok ? "ok" : "fail";
                e.Detail = !applies ? "the game has no such command for " + target.Aircraft : ok ? string.Empty : "not where a hand is in flight (grip / throttle, no modifier, fitting input)";
                row.Essentials.Add(e);
            }
        }

        // The popular setups' rows that pass each hard gate ("gate:look") and reach each in-flight essential ("essential:ptt"),
        // and the destructive bindings: the per-gate non-regression baseline (the popular setups are fixed, so the counts
        // repeat exactly; a drop in look, PTT or flaps fails even when the averages hold).
        internal static Dictionary<string, int> GateCounts(IEnumerable<EvalRow> rows)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int destructive = 0;
            foreach (EvalRow row in rows)
            {
                foreach (EvalGate gate in row.Gates.Where(g => g.Status == "ok" || g.Status == "n/a"))
                    counts["gate:" + gate.Gate] = (counts.ContainsKey("gate:" + gate.Gate) ? counts["gate:" + gate.Gate] : 0) + 1;
                foreach (EvalEssential e in row.Essentials.Where(e => e.Status == "ok"))
                    counts["essential:" + e.Id] = (counts.ContainsKey("essential:" + e.Id) ? counts["essential:" + e.Id] : 0) + 1;
                destructive += row.Destructive.Count;
            }
            counts["destructive"] = destructive;
            return counts;
        }

        // Every gate / essential with fewer passing rows than the baseline, and more destructive bindings.
        internal static List<string> GateRegressions(Dictionary<string, object> baseline, Dictionary<string, int> now)
        {
            List<string> failures = new List<string>();
            foreach (KeyValuePair<string, object> pair in baseline.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                int was = Convert.ToInt32(pair.Value, CultureInfo.InvariantCulture);
                int count;
                now.TryGetValue(pair.Key, out count);
                if (pair.Key == "destructive" ? count > was : count < was)
                    failures.Add("per-gate non-regression: " + pair.Key + " " + count.ToString(CultureInfo.InvariantCulture) + " (baseline " + was.ToString(CultureInfo.InvariantCulture) + ") on the popular setups");
            }
            return failures;
        }

        // For the ACE COMBAT 8 rows (PadScore): the pad gates are the essentials.
        internal static void PadEssentials(EvalRow row)
        {
            row.EssentialClass = "pad";
            row.Essentials.Clear();
            foreach (string id in EssentialsOfClass["pad"])
            {
                string gateId = id == "target" ? "pad target" : id;
                EvalGate gate = row.Gates.LastOrDefault(g => g.Gate == gateId);
                row.Essentials.Add(new EvalEssential { Id = id, Critical = !AxisEssentials.Contains(id), Status = gate == null ? "n/a" : gate.Status, Detail = gate == null ? "not checked for the pad" : gate.Detail });
            }
        }

        // Per row: share of the essentials that apply which are reached; whether every time-critical one is.
        internal static double EssentialPct(EvalRow row)
        {
            List<EvalEssential> applies = row.Essentials.Where(e => e.Status != "n/a").ToList();
            return applies.Count == 0 ? 100 : 100.0 * applies.Count(e => e.Status == "ok") / applies.Count;
        }

        internal static bool CriticalComplete(EvalRow row)
        {
            return row.Error.Length == 0 && row.Essentials.Where(e => e.Critical && e.Status != "n/a").All(e => e.Status == "ok");
        }

        internal static string EssentialsSummary(IEnumerable<EvalRow> rows)
        {
            List<EvalRow> list = rows.Where(r => r.Essentials.Count > 0).ToList();
            if (list.Count == 0)
                return "no rows";
            double weight = list.Sum(r => r.Weight);
            double pct = weight <= 0 ? 0 : list.Sum(r => r.Weight * EssentialPct(r)) / weight;
            double complete = weight <= 0 ? 0 : 100.0 * list.Where(CriticalComplete).Sum(r => r.Weight) / weight;
            return "in-flight essentials reached " + Pct(pct) + " %, rows with every time-critical essential " + Pct(complete) + " % (" + list.Count.ToString(CultureInfo.InvariantCulture) + " rows)";
        }

        internal static void EssentialsMarkdown(StringBuilder sb, EvalResult result)
        {
            sb.AppendLine("## In-flight essentials (headline)");
            sb.AppendLine();
            sb.AppendLine("Per row: the share of the essentials of its game class that a pilot can work in flight (grip or throttle side without a modifier for the " +
                          "time-critical ones, a fitting input in the right direction, nothing destructive), and the share of rows where every time-critical essential is " +
                          "there. The lists per class: docs/auto-setup-eval.md#in-flight-essentials.");
            sb.AppendLine();
            sb.AppendLine("| Stage | Kind | Rows | Essentials reached % | Rows with every time-critical essential % |");
            sb.AppendLine("|---|---|---|---|---|");
            Action<string, IEnumerable<EvalRow>> line = (stage, rows) =>
            {
                foreach (string kind in new[] { "popular", "random", "regression" })
                {
                    List<EvalRow> list = rows.Where(r => r.SetupKind == kind && r.Essentials.Count > 0).ToList();
                    if (list.Count == 0)
                        continue;
                    double weight = list.Sum(r => r.Weight);
                    sb.AppendLine("| " + stage + " | " + kind + " | " + list.Count.ToString(CultureInfo.InvariantCulture) + " | " + Pct(list.Sum(r => r.Weight * EssentialPct(r)) / weight) + " | " +
                                  Pct(100.0 * list.Where(CriticalComplete).Sum(r => r.Weight) / weight) + " |");
                }
            };
            line("Setup alone", result.Rows);
            line("Typical DCS hangar", result.TypicalRows);
            line("ACE COMBAT 8 alone", result.PadRows);
            line("All games before Auto Group", result.PipelinePlanRows);
            line("After Auto Group + Fill all", result.PipelineRows);
            sb.AppendLine();
            sb.AppendLine("Essentials missed most (setup alone, popular + random):");
            sb.AppendLine();
            foreach (var miss in result.Rows.Where(r => r.SetupKind != "regression").SelectMany(r => r.Essentials.Where(e => e.Status == "fail").Select(e => r.EssentialClass + " " + e.Id))
                         .GroupBy(x => x).OrderByDescending(g => g.Count()).Take(12))
                sb.AppendLine("- " + miss.Key + ": " + miss.Count().ToString(CultureInfo.InvariantCulture) + " rows");
            sb.AppendLine();
        }
    }
}
