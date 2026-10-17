using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine
{
    // binds detent merge: per-aircraft throttle relations on one device axis (each with its own curve for a different
    // afterburner start) become one relation in detent mode. The aircraft's afterburner start is read from the curve it
    // had: its game throttle at the device's detent; the device's detent from a relation that gives full power there
    // (aircraft without afterburner), else from the gate data. Without --apply only the plan is shown; with --apply the
    // profile changes (one history version: profile history undo takes it back).
    internal static partial class Program
    {
        private static readonly Regex ThrottleName = new Regex("throttle|thrust|schub|gas\\b|power lever|engine power", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // DCS thrust axes: iCommandPlaneThrustCommon / ThrustLeft / ThrustRight.
        private static readonly Regex DcsThrustNode = new Regex("^a200[456]cdnil$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private sealed class DetentMergeSource
        {
            public string Relation;
            public Bind Bind;
            public Relation Rel;
            public Func<double, double> Throttle;
            public double FullAt = double.NaN;
            public int Planes;
        }

        private sealed class DetentMergePlan
        {
            public string DeviceKey;
            public string DeviceLabel;
            public string Into;
            public List<DetentMergeSource> Sources = new List<DetentMergeSource>();
            public DetentCalibration Calibration;
            public bool CalibrationInferred;
            public string CalibrationNote = string.Empty;
            public DetentTargetKind NoAfterburner = DetentTargetKind.Linear;
            public List<Dictionary<string, object>> Targets = new List<Dictionary<string, object>>();
            public Dictionary<string, string> Own = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Notes = new List<string>();
            public List<string> Problems = new List<string>();
        }

        internal static bool IsThrottleRelation(string name, Relation relation)
        {
            if (relation == null || !relation.ISAXIS)
                return false;
            if (ThrottleName.IsMatch(name ?? string.Empty))
                return true;
            return relation.NODES != null && relation.NODES.Any(n => n != null && (string.IsNullOrWhiteSpace(n.Game) || n.Game == "DCS") &&
                DcsThrustNode.IsMatch(n.ID ?? string.Empty) && n.GetActiveAircraftList().Count > 0);
        }

        private static int DetentMerge(List<string> args, string outPath, bool textOutput)
        {
            bool apply = PopFlag(args, "--apply");
            string relationsRaw = PopOptionValue(args, "--relations");
            string into = PopOptionValue(args, "--into");
            string abSource = (PopOptionValue(args, "--ab-source") ?? "profile").Trim().ToLowerInvariant();
            if (args.Count > 0)
                return Fail("Unknown binds detent merge argument(s): " + string.Join(" ", args));
            if (abSource != "profile" && abSource != "game")
                return Fail("--ab-source takes profile (the afterburner start the old curves gave) or game (the gate data where it has a value).");

            List<List<string>> groups = new List<List<string>>();
            if (!string.IsNullOrWhiteSpace(relationsRaw))
            {
                List<string> names = new List<string>();
                foreach (string raw in relationsRaw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    List<string> candidates;
                    string name;
                    if (!TryResolveRelationOrBindName(raw.Trim(), out name, out candidates))
                        return FailResolution("relation/bind", raw.Trim(), candidates);
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        names.Add(name);
                }
                if (names.Count < 2)
                    return Fail("binds detent merge needs at least two relations.");
                groups.Add(names);
            }
            else
            {
                // Every device axis with at least two bound throttle relations.
                foreach (IGrouping<string, string> axis in InternalDataManagement.AllRelations
                    .Where(r => IsThrottleRelation(r.Key, r.Value))
                    .Where(r => { Bind b; return InternalDataManagement.AllBinds.TryGetValue(r.Key, out b) && b != null && !string.IsNullOrWhiteSpace(b.JAxis) && !string.IsNullOrWhiteSpace(b.Joystick); })
                    .Select(r => r.Key)
                    .GroupBy(r => EngineAxisDetents.DeviceKey(InternalDataManagement.AllBinds[r]), StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    List<string> names = axis.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                    if (names.Count >= 2)
                        groups.Add(names);
                }
                if (!string.IsNullOrWhiteSpace(into))
                    return Fail("--into needs --relations.");
            }

            List<DetentMergePlan> plans = new List<DetentMergePlan>();
            foreach (List<string> names in groups)
            {
                DetentMergePlan plan = PlanDetentMerge(names, into, abSource);
                plans.Add(plan);
            }
            if (apply)
            {
                if (plans.Count == 0)
                    return Fail("Nothing to merge: no device axis has two or more throttle relations.");
                List<string> problems = plans.SelectMany(p => p.Problems).ToList();
                if (problems.Count > 0)
                    return Fail("Cannot merge: " + string.Join(" ", problems));
                foreach (DetentMergePlan plan in plans)
                    ApplyDetentMerge(plan);
                EngineProfileHistory.NoCoalesce = true;
                EngineProfileHistory.Note = "Detent mode: merged " + string.Join("; ", plans.Select(p => string.Join(", ", p.Sources.Select(s => s.Relation)) + " into " + p.Into));
            }
            object data = new
            {
                Applied = apply,
                AbSource = abSource,
                Proposals = plans.Select(p => new
                {
                    Device = p.DeviceKey,
                    p.DeviceLabel,
                    p.Into,
                    Sources = p.Sources.Select(s => new
                    {
                        s.Relation,
                        s.Planes,
                        AtDetent = p.Calibration == null ? (double?)null : Math.Round(s.Throttle(p.Calibration.Detent), 4),
                        FullAt = double.IsNaN(s.FullAt) ? (double?)null : Math.Round(s.FullAt, 4)
                    }).ToList(),
                    Calibration = p.Calibration == null ? null : new
                    {
                        p.Calibration.Idle,
                        p.Calibration.Detent,
                        DetentTravel = Math.Round(p.Calibration.Travel(p.Calibration.Detent), 4),
                        Inferred = p.CalibrationInferred,
                        Note = p.CalibrationNote,
                        Text = p.Calibration.Format()
                    },
                    NoAfterburner = DetentSettings.KindText(p.NoAfterburner),
                    Targets = p.Targets,
                    p.Notes,
                    p.Problems
                }).ToList()
            };
            if (!textOutput)
            {
                EmitPayload(EngineSerialization.ToJson(data), outPath);
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            if (plans.Count == 0)
                sb.AppendLine("No device axis has two or more throttle relations.");
            foreach (DetentMergePlan plan in plans)
            {
                sb.AppendLine((apply ? "Merged " : "Would merge ") + string.Join(", ", plan.Sources.Select(s => s.Relation)) + " into '" + plan.Into + "' (" + plan.DeviceLabel + ")");
                if (plan.Calibration != null)
                    sb.AppendLine("  Device detent: " + EngineAxisDetents.Pct(plan.Calibration.Travel(plan.Calibration.Detent)) + " of the travel" +
                        (plan.CalibrationInferred ? " (" + plan.CalibrationNote + ")" : " (calibrated)"));
                foreach (Dictionary<string, object> t in plan.Targets)
                    sb.AppendLine("  " + t["Game"] + " / " + t["Title"] + ": " + t["Describe"] + " (from " + t["From"] + ")" +
                        (t["GameData"] != null ? ", game data " + EngineAxisDetents.Pct((double)t["GameData"]) : string.Empty));
                foreach (string note in plan.Notes)
                    sb.AppendLine("  Note: " + note);
                foreach (string problem in plan.Problems)
                    sb.AppendLine("  Problem: " + problem);
            }
            if (!apply && plans.Count > 0)
                sb.AppendLine("Run again with --apply to merge (profile history undo takes it back).");
            EmitPayload(sb.ToString(), outPath);
            return 0;
        }

        private static DetentMergePlan PlanDetentMerge(List<string> names, string into, string abSource)
        {
            DetentMergePlan plan = new DetentMergePlan();
            AxisModel dcs = AxisModels.Get(AxisModels.Dcs);
            foreach (string name in names)
            {
                Relation relation;
                Bind bind;
                if (!InternalDataManagement.AllRelations.TryGetValue(name, out relation) || relation == null || !relation.ISAXIS)
                {
                    plan.Problems.Add("'" + name + "' is not an axis relation.");
                    continue;
                }
                if (!InternalDataManagement.AllBinds.TryGetValue(name, out bind) || bind == null || string.IsNullOrWhiteSpace(bind.JAxis))
                {
                    plan.Problems.Add("'" + name + "' has no device axis bound.");
                    continue;
                }
                AxisTuning baseTuning = BindTuning.Base(bind);
                // DCS thrust axes give full thrust at -1 (the relation's Invert decides which device end that is).
                DetentMergeSource source = new DetentMergeSource
                {
                    Relation = name,
                    Bind = bind,
                    Rel = relation,
                    Throttle = u => (1.0 - dcs.Evaluate(baseTuning, 2.0 * u - 1.0)) / 2.0,
                    Planes = EngineAxisDetents.RelationTargets(relation).Count
                };
                plan.Sources.Add(source);
            }
            if (plan.Sources.Count == 0)
                return plan;
            plan.DeviceKey = EngineAxisDetents.DeviceKey(plan.Sources[0].Bind);
            plan.DeviceLabel = DetentDeviceLabel(plan.Sources[0].Bind);
            if (plan.Sources.Any(s => !string.Equals(EngineAxisDetents.DeviceKey(s.Bind), plan.DeviceKey, StringComparison.OrdinalIgnoreCase)))
                plan.Problems.Add("The relations are bound to different device axes; merge only relations of one throttle axis.");

            // Which end is full: most curves rise towards it.
            int rising = plan.Sources.Count(s => s.Throttle(1.0) > s.Throttle(0.0));
            bool increasing = rising * 2 >= plan.Sources.Count;
            Func<DetentMergeSource, Func<double, double>> byTravel = s => t => s.Throttle(increasing ? t : 1.0 - t);
            foreach (DetentMergeSource s in plan.Sources)
            {
                double at = DetentMath.DetentFromFullCurve(byTravel(s));
                s.FullAt = double.IsNaN(at) ? double.NaN : (increasing ? at : 1.0 - at);
            }

            DetentCalibration calibration = EngineAxisDetents.CalibrationFor(plan.Sources[0].Bind);
            if (calibration != null && calibration.Validate().Length == 0)
                plan.Calibration = calibration;
            else
            {
                List<double> fromFull = plan.Sources.Where(s => !double.IsNaN(s.FullAt)).Select(s => s.FullAt).OrderBy(v => v).ToList();
                double detent = double.NaN;
                if (fromFull.Count > 0)
                {
                    detent = fromFull[fromFull.Count / 2];
                    plan.CalibrationNote = "read from the profile: " + string.Join(", ", plan.Sources.Where(s => !double.IsNaN(s.FullAt)).Select(s => s.Relation)) +
                                           " gives full power there; confirm it with the detent wizard";
                }
                else
                {
                    List<double> fromData = new List<double>();
                    foreach (DetentMergeSource s in plan.Sources)
                    {
                        foreach (KeyValuePair<string, string> t in EngineAxisDetents.RelationTargets(s.Rel))
                        {
                            ThrottleGate gate = EngineAxisDetents.FindGate(t.Key, t.Value);
                            if (gate == null || gate.NoAfterburner || double.IsNaN(gate.Afterburner) || t.Key != "DCS")
                                continue;
                            double u = DetentMath.PositionOf(s.Throttle, gate.Afterburner);
                            if (!double.IsNaN(u))
                                fromData.Add(u);
                        }
                    }
                    fromData.Sort();
                    if (fromData.Count > 0)
                    {
                        detent = fromData[fromData.Count / 2];
                        plan.CalibrationNote = "read from the profile's curves and the gate data (median of " + fromData.Count.ToString(CultureInfo.InvariantCulture) + " aircraft); confirm it with the detent wizard";
                    }
                }
                if (double.IsNaN(detent))
                    plan.Problems.Add("The device's detent could not be read from the profile: calibrate the throttle axis with the detent wizard first.");
                else
                {
                    plan.Calibration = new DetentCalibration
                    {
                        Idle = increasing ? 0.0 : 1.0,
                        Detent = Math.Round(detent, 4),
                        Source = "profile",
                        When = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    };
                    plan.CalibrationInferred = true;
                    string error = plan.Calibration.Validate();
                    if (error.Length > 0)
                        plan.Problems.Add("The detent read from the profile is not usable: " + error);
                }
            }

            // The relation that stays: the one with most aircraft among those that do not give full power at the detent.
            DetentMergeSource keep = null;
            if (!string.IsNullOrWhiteSpace(into))
            {
                keep = plan.Sources.FirstOrDefault(s => string.Equals(s.Relation, into.Trim(), StringComparison.OrdinalIgnoreCase));
                if (keep == null)
                    plan.Problems.Add("--into must be one of the merged relations.");
            }
            if (keep == null && plan.Calibration != null)
                keep = plan.Sources.Where(s => s.Throttle(plan.Calibration.Detent) < 0.97).OrderByDescending(s => s.Planes).ThenBy(s => s.Relation, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (keep == null)
                keep = plan.Sources.OrderByDescending(s => s.Planes).First();
            plan.Into = keep.Relation;
            plan.NoAfterburner = plan.Calibration != null && plan.Sources.Any(s => s.Throttle(plan.Calibration.Detent) >= 0.97)
                ? DetentTargetKind.FullAtDetent : DetentTargetKind.Linear;
            if (plan.Calibration == null)
                return plan;

            // Per aircraft: what its old curve gave at the device's detent.
            DetentSettings defaults = new DetentSettings { NoAfterburner = plan.NoAfterburner };
            HashSet<string> done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DetentMergeSource s in plan.Sources.OrderBy(s => s == keep ? 0 : 1))
            {
                foreach (KeyValuePair<string, string> t in EngineAxisDetents.RelationTargets(s.Rel))
                {
                    string key = DetentSettings.TargetKey(t.Key, t.Value);
                    if (!done.Add(key))
                    {
                        plan.Notes.Add(t.Key + " / " + t.Value + " is in more than one of the relations; the values of the first are used.");
                        continue;
                    }
                    ThrottleGate gate = EngineAxisDetents.FindGate(t.Key, t.Value);
                    bool dataHasAb = gate != null && !gate.NoAfterburner && !double.IsNaN(gate.Afterburner);
                    bool dataNoAb = gate != null && gate.NoAfterburner;
                    double atDetent = s.Throttle(plan.Calibration.Detent);
                    DetentTarget target;
                    string from;
                    if (atDetent >= 0.97)
                    {
                        if (dataHasAb)
                        {
                            target = DetentTarget.Afterburner(gate.Afterburner, gate.IdleGate);
                            from = "game data (the old curve gave full power at the detent)";
                        }
                        else
                        {
                            target = DetentTarget.Full;
                            from = "profile";
                        }
                    }
                    else if (dataNoAb || !EngineAxisDetents.HasPlaneCurves(t.Key) && !dataHasAb)
                    {
                        target = DetentTarget.Linear;
                        from = "profile (no afterburner, the old curve went on past the detent)";
                    }
                    else if (s == keep && !dataHasAb)
                    {
                        // The relation that stays keeps its curve: for an aircraft JoyPro has no gate data for that is
                        // the same as an afterburner at the old curve's value, without claiming one.
                        target = DetentTarget.Linear;
                        from = "profile (the curve of '" + keep.Relation + "' as it was)";
                    }
                    else if (abSource == "game" && dataHasAb)
                    {
                        target = DetentTarget.Afterburner(gate.Afterburner, gate.IdleGate);
                        from = "game data";
                    }
                    else
                    {
                        target = DetentTarget.Afterburner(Math.Round(Math.Min(1.0, atDetent + defaults.Margin), 3), gate == null ? 0 : gate.IdleGate);
                        from = "profile";
                    }
                    string source;
                    ThrottleGate unused;
                    DetentTarget byDefault = EngineAxisDetents.TargetFor(defaults, t.Key, t.Value, out source, out unused);
                    if (!target.SameAs(byDefault))
                        plan.Own[key] = target.Format();
                    plan.Targets.Add(new Dictionary<string, object>
                    {
                        { "Game", t.Key },
                        { "Plane", t.Value },
                        { "Title", DetentPlaneTitle(t.Key, t.Value, gate) },
                        { "Relation", s.Relation },
                        { "Target", target.Format() },
                        { "Describe", target.Describe() },
                        { "From", from },
                        { "AtDetent", Math.Round(atDetent, 4) },
                        { "GameData", dataHasAb ? (object)gate.Afterburner : null },
                        { "Confidence", gate == null ? string.Empty : gate.Confidence }
                    });
                }
                foreach (string scope in (s.Bind.TuningOverrides ?? new Dictionary<string, string>()).Keys.Where(k => k.StartsWith("plane:", StringComparison.OrdinalIgnoreCase)))
                {
                    if (s != keep)
                        plan.Notes.Add("'" + s.Relation + "' has its own tuning for " + scope + "; detent mode replaces it.");
                }
            }
            return plan;
        }

        private static void ApplyDetentMerge(DetentMergePlan plan)
        {
            DetentMergeSource keep = plan.Sources.First(s => s.Relation == plan.Into);
            Relation target = keep.Rel;
            foreach (DetentMergeSource s in plan.Sources.Where(s => s != keep))
            {
                foreach (RelationItem node in s.Rel.NODES ?? new List<RelationItem>())
                {
                    if (node == null || node.GetActiveAircraftList().Count == 0)
                        continue;
                    RelationItem copy = node.Copy();
                    foreach (string plane in copy.AIRCRAFT.Keys.ToList())
                    {
                        if (!copy.AIRCRAFT[plane])
                            copy.AIRCRAFT.Remove(plane);
                    }
                    target.IncludeRelationItem(copy);
                }
                if (target.Groups == null)
                    target.Groups = new List<string>();
                foreach (string group in s.Rel.Groups ?? new List<string>())
                {
                    if (!string.IsNullOrWhiteSpace(group) && !target.Groups.Contains(group, StringComparer.OrdinalIgnoreCase))
                        target.Groups.Add(group);
                }
                RemoveRelationByName(s.Relation);
            }
            target.RecalculateElementCount();
            DetentSettings settings = EngineAxisDetents.SettingsOf(keep.Bind) ?? new DetentSettings();
            settings.On = true;
            settings.NoAfterburner = plan.NoAfterburner;
            foreach (KeyValuePair<string, string> own in plan.Own)
                settings.Targets[own.Key] = own.Value;
            EngineAxisDetents.SetSettings(keep.Bind, settings);
            if (plan.CalibrationInferred && plan.Calibration != null)
                EngineAxisDetents.SetCalibration(keep.Bind, plan.Calibration);
        }
    }
}
