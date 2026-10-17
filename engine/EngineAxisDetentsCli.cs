using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine
{
    // engine binds detent ...: detent mode for throttles (docs/axis-curves.md "Detent mode").
    //   binds detent show <relation>                           settings, calibration, aircraft groups and per-game fits
    //   binds detent set <relation> [--on|--off] [--no-ab full|linear] [--unknown full|linear] [--margin m]
    //                    [--target <game>:<plane>=ab:0.78|full|linear|default]...
    //   binds detent calibrate <relation> --idle u --detent u [--full u|none] [--cutoff u|none] [--source s]
    //   binds detent uncalibrate <relation>
    //   binds detent gates [--game G] [--plane P]              the shipped gate data
    //   binds detent merge [--relations a,b,...] [--into name] [--ab-source profile|game] [--apply]
    internal static partial class Program
    {
        private static int RunBindDetent(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count > 1 ? command[1].ToLowerInvariant() : "show";
            List<string> args = command.Skip(2).ToList();
            try
            {
                switch (action)
                {
                    case "gates":
                    case "data":
                        return DetentGates(args, outPath, textOutput);
                    case "merge":
                        return DetentMerge(args, outPath, textOutput);
                    case "show":
                    case "set":
                    case "calibrate":
                    case "uncalibrate":
                        break;
                    default:
                        return Fail("binds detent: unknown action '" + action + "' (show, set, calibrate, uncalibrate, gates, merge).");
                }
                if (args.Count < 1)
                    return Fail("binds detent " + action + " requires a relation or bind name.");
                string query = args[0];
                args.RemoveAt(0);
                List<string> candidates;
                string relationName;
                if (!TryResolveRelationOrBindName(query, out relationName, out candidates))
                    return FailResolution("relation/bind", query, candidates);
                Relation relation;
                if (!InternalDataManagement.AllRelations.TryGetValue(relationName, out relation) || relation == null)
                    return Fail("Relation not found: " + relationName);
                if (!relation.ISAXIS)
                    return Fail("Detent mode is only for axis relations; '" + relationName + "' is a button relation.");
                Bind bind;
                InternalDataManagement.AllBinds.TryGetValue(relationName, out bind);
                if (action == "show")
                {
                    if (args.Count > 0)
                        return Fail("Unknown binds detent show argument(s): " + string.Join(" ", args));
                    return DetentShow(relationName, relation, bind, outPath, textOutput);
                }
                if (bind == null || string.IsNullOrWhiteSpace(bind.Joystick) || string.IsNullOrWhiteSpace(bind.JAxis))
                    return Fail("Relation '" + relationName + "' has no device axis bound; bind the throttle axis first.");
                if (action == "set")
                    return DetentSet(relationName, relation, bind, args, outPath, textOutput);
                if (action == "calibrate")
                    return DetentCalibrate(relationName, relation, bind, args, outPath, textOutput);
                if (args.Count > 0)
                    return Fail("Unknown binds detent uncalibrate argument(s): " + string.Join(" ", args));
                bool had = EngineAxisDetents.CalibrationFor(bind) != null;
                EngineAxisDetents.SetCalibration(bind, null);
                return DetentShowAfter(relationName, relation, bind, had ? "Calibration of " + DetentDeviceLabel(bind) + " removed." : "No calibration stored.", outPath, textOutput);
            }
            catch (ArgumentException ex)
            {
                return Fail(ex.Message);
            }
        }

        private static string DetentDeviceLabel(Bind bind)
        {
            string device = (bind.Joystick ?? string.Empty).Trim();
            int brace = device.IndexOf('{');
            if (brace > 0)
                device = device.Substring(0, brace).Trim();
            return device + " " + (bind.JAxis ?? string.Empty).Trim();
        }

        private static int DetentSet(string relationName, Relation relation, Bind bind, List<string> args, string outPath, bool textOutput)
        {
            bool on = PopFlag(args, "--on");
            bool off = PopFlag(args, "--off");
            string noAb = PopOptionValue(args, "--no-ab");
            string unknown = PopOptionValue(args, "--unknown");
            string marginRaw = PopOptionValue(args, "--margin");
            List<string> targets = PopOptionValues(args, "--target");
            if (args.Count > 0)
                return Fail("Unknown binds detent set argument(s): " + string.Join(" ", args));
            if (on && off)
                return Fail("Use --on or --off, not both.");
            DetentSettings settings = EngineAxisDetents.SettingsOf(bind) ?? new DetentSettings();
            List<string> changes = new List<string>();
            if (on)
                settings.On = true;
            if (off)
                settings.On = false;
            DetentTargetKind kind;
            if (noAb != null)
            {
                if (!DetentSettings.TryKind(noAb, out kind))
                    return Fail("--no-ab takes full (full power at the detent) or linear.");
                settings.NoAfterburner = kind;
            }
            if (unknown != null)
            {
                if (!DetentSettings.TryKind(unknown, out kind))
                    return Fail("--unknown takes full or linear.");
                settings.Unknown = kind;
            }
            if (marginRaw != null)
            {
                double margin;
                if (!double.TryParse(marginRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out margin) || margin < 0 || margin > 0.1)
                    return Fail("--margin must be 0..0.1 (share of the game's throttle).");
                settings.Margin = margin;
            }
            foreach (string raw in targets)
            {
                int eq = raw.LastIndexOf('=');
                int colon = raw.IndexOf(':');
                if (eq <= 0 || colon <= 0 || colon > eq)
                    return Fail("--target takes <game>:<plane>=ab:0.78|full|linear|default, e.g. --target DCS:F-16C_50=ab:0.79.");
                string key = DetentSettings.TargetKey(raw.Substring(0, colon), raw.Substring(colon + 1, eq - colon - 1));
                string value = raw.Substring(eq + 1).Trim();
                if (string.Equals(value, "default", StringComparison.OrdinalIgnoreCase))
                {
                    settings.Targets.Remove(key);
                    continue;
                }
                DetentTarget target = DetentTarget.Parse(value);
                if (target == null)
                    return Fail("--target value must be ab:<0..1>[/idle:<0..1>], full, linear or default: " + value);
                settings.Targets[key] = target.Format();
            }
            EngineAxisDetents.SetSettings(bind, settings);
            string message = "Detent mode " + (settings.On ? "on" : "off") + " for " + relationName + ".";
            if (settings.On && EngineAxisDetents.CalibrationFor(bind) == null)
                message += " The device axis is not calibrated yet: run binds detent calibrate (or the GUI's wizard) before it takes effect.";
            return DetentShowAfter(relationName, relation, bind, message, outPath, textOutput);
        }

        private static int DetentCalibrate(string relationName, Relation relation, Bind bind, List<string> args, string outPath, bool textOutput)
        {
            string idleRaw = PopOptionValue(args, "--idle");
            string detentRaw = PopOptionValue(args, "--detent");
            string fullRaw = PopOptionValue(args, "--full");
            string cutoffRaw = PopOptionValue(args, "--cutoff");
            string source = PopOptionValue(args, "--source");
            if (args.Count > 0)
                return Fail("Unknown binds detent calibrate argument(s): " + string.Join(" ", args));
            DetentCalibration calibration = new DetentCalibration
            {
                Source = string.IsNullOrWhiteSpace(source) ? "manual" : source.Trim().ToLowerInvariant(),
                When = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            };
            double value;
            if (idleRaw == null || !TryParseUnit(idleRaw, out value))
                return Fail("--idle must be the raw axis position 0..1 at idle.");
            calibration.Idle = value;
            if (detentRaw == null || !TryParseUnit(detentRaw, out value))
                return Fail("--detent must be the raw axis position 0..1 at the afterburner detent.");
            calibration.Detent = value;
            if (fullRaw != null && !string.Equals(fullRaw, "none", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseUnit(fullRaw, out value))
                    return Fail("--full must be 0..1 or none.");
                calibration.Full = value;
            }
            if (cutoffRaw != null && !string.Equals(cutoffRaw, "none", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseUnit(cutoffRaw, out value))
                    return Fail("--cutoff must be 0..1 or none.");
                calibration.Cutoff = value;
            }
            string error = calibration.Validate();
            if (error.Length > 0)
                return Fail(error);
            EngineAxisDetents.SetCalibration(bind, calibration);
            return DetentShowAfter(relationName, relation, bind, "Calibrated " + DetentDeviceLabel(bind) + ": idle at " + EngineAxisDetents.Pct(calibration.Travel(calibration.Idle)) +
                ", detent at " + EngineAxisDetents.Pct(calibration.Travel(calibration.Detent)) + " of the travel.", outPath, textOutput);
        }

        private static int DetentShowAfter(string relationName, Relation relation, Bind bind, string message, string outPath, bool textOutput)
        {
            return DetentShow(relationName, relation, bind, outPath, textOutput, message);
        }

        // ---- show ---------------------------------------------------------------------------------------------------

        private sealed class DetentGroupView
        {
            public string Key;
            public DetentTarget Target;
            public List<DetentAircraftView> Aircraft = new List<DetentAircraftView>();
        }

        private sealed class DetentAircraftView
        {
            public string Game;
            public string Plane;
            public string Title;
            public string Source;
            public string Confidence;
            public double? GameData;
            public string GameDataSource;
        }

        private static string DetentPlaneTitle(string game, string plane, ThrottleGate gate)
        {
            if (gate != null && gate.Plane != "*" && gate.Title.Length > 0)
            {
                if (plane.EndsWith("_easy", StringComparison.OrdinalIgnoreCase))
                    return gate.Title + " (easy)";
                return string.Equals(gate.Plane, plane, StringComparison.OrdinalIgnoreCase) ? gate.Title : gate.Title + " (" + plane + ")";
            }
            if (string.Equals(game, plane, StringComparison.OrdinalIgnoreCase))
                return GameDisplayName(game);
            return plane;
        }

        private static string GameDisplayName(string game)
        {
            JoyPro.Engine.Games.IGameIntegration integration;
            if (JoyPro.Engine.Games.GameRegistry.TryResolve(game, out integration) && integration != null)
                return integration.DisplayName;
            return game;
        }

        internal static object DetentShowData(string relationName, Relation relation, Bind bind, string message)
        {
            DetentSettings settings = EngineAxisDetents.SettingsOf(bind);
            DetentSettings effective = settings ?? new DetentSettings { On = false };
            DetentCalibration calibration = EngineAxisDetents.CalibrationFor(bind);
            List<string> warnings = new List<string>();
            if (bind == null || string.IsNullOrWhiteSpace(bind.JAxis))
                warnings.Add("No device axis bound.");
            else if (calibration == null)
                warnings.Add("The device axis is not calibrated: run the detent wizard (binds detent calibrate).");
            AxisTuning baseTuning = BindTuning.Base(bind);
            if (!baseTuning.Slider)
                warnings.Add("The relation is not a one-sided axis (Slider): detent mode switches the slider on for the curves it writes.");

            List<DetentGroupView> groups = new List<DetentGroupView>();
            foreach (KeyValuePair<string, string> t in EngineAxisDetents.RelationTargets(relation))
            {
                string source;
                ThrottleGate gate;
                DetentTarget target = EngineAxisDetents.TargetFor(effective, t.Key, t.Value, out source, out gate);
                string key = target.Format();
                DetentGroupView group = groups.FirstOrDefault(g => g.Key == key);
                if (group == null)
                {
                    group = new DetentGroupView { Key = key, Target = target };
                    groups.Add(group);
                }
                DetentTarget data = gate == null ? null : gate.Target(effective);
                group.Aircraft.Add(new DetentAircraftView
                {
                    Game = t.Key,
                    Plane = t.Value,
                    Title = DetentPlaneTitle(t.Key, t.Value, gate),
                    Source = source,
                    Confidence = gate == null ? string.Empty : gate.Confidence,
                    GameData = gate == null || gate.NoAfterburner || double.IsNaN(gate.Afterburner) ? (double?)null : gate.Afterburner,
                    GameDataSource = gate == null ? string.Empty : gate.Source
                });
            }
            groups = groups.OrderBy(g => g.Target.Kind == DetentTargetKind.Afterburner ? 0 : g.Target.Kind == DetentTargetKind.FullAtDetent ? 1 : 2)
                .ThenBy(g => g.Target.AbStart).ToList();

            List<object> groupData = new List<object>();
            foreach (DetentGroupView group in groups)
            {
                List<object> games = new List<object>();
                foreach (string game in group.Aircraft.Select(a => a.Game).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    ResolvedTuning normal = BindTuning.ResolveWithoutDetent(bind, game, EngineAxisDetents.HasPlaneCurves(game) ? group.Aircraft.First(a => a.Game == game).Plane : null);
                    object fitData = null;
                    if (calibration != null && calibration.Validate().Length == 0 && group.Target.Kind != DetentTargetKind.Linear && normal.Source == BindTuning.BaseScope)
                    {
                        DetentTarget used = group.Target;
                        List<string> differing = new List<string>();
                        if (!EngineAxisDetents.HasPlaneCurves(game))
                            used = EngineAxisDetents.GameTarget(effective, relation, game, out differing);
                        DetentFit fit = EngineAxisDetents.FitFor(normal.Model, normal.Tuning, calibration, used, effective.Margin);
                        AxisTuning curve = fit.Fallback || fit.Tuning == null ? normal.Tuning : fit.Tuning;
                        string warning = fit.Warning;
                        if (differing.Count > 0 && !used.SameAs(group.Target))
                            warning = (warning + " " + GameDisplayName(game) + " has one curve for all aircraft; it gets: " + used.Describe() + ".").Trim();
                        fitData = new
                        {
                            fit.Method,
                            MaxError = Math.Round(fit.MaxError, 5),
                            fit.Fallback,
                            Warning = warning,
                            Tuning = curve.Format(),
                            Throttle = DetentThrottleSamples(normal.Model, curve, calibration)
                        };
                    }
                    games.Add(new
                    {
                        Game = game,
                        Title = GameDisplayName(game),
                        Model = normal.Model.Id,
                        OwnScope = normal.Source == BindTuning.BaseScope ? string.Empty : normal.Source,
                        PlaneCurves = EngineAxisDetents.HasPlaneCurves(game),
                        Fit = fitData
                    });
                }
                groupData.Add(new
                {
                    Target = group.Key,
                    Kind = group.Target.Kind.ToString(),
                    AbStart = group.Target.Kind == DetentTargetKind.Afterburner ? Math.Round(group.Target.AbStart, 4) : (double?)null,
                    IdleGate = Math.Round(group.Target.IdleGate, 4),
                    Describe = group.Target.Describe(),
                    Label = string.Join(", ", group.Aircraft.Select(a => a.Title).Distinct(StringComparer.OrdinalIgnoreCase)),
                    Aircraft = group.Aircraft.Select(a => new { a.Game, a.Plane, a.Title, a.Source, a.Confidence, a.GameData, a.GameDataSource }).ToList(),
                    Games = games
                });
            }
            return new
            {
                Relation = relationName,
                Message = message ?? string.Empty,
                Device = bind == null ? string.Empty : (bind.Joystick ?? string.Empty).Trim(),
                Axis = bind == null ? string.Empty : (bind.JAxis ?? string.Empty).Trim(),
                DeviceLabel = bind == null ? string.Empty : DetentDeviceLabel(bind),
                Settings = new
                {
                    Stored = settings != null,
                    effective.On,
                    NoAfterburner = DetentSettings.KindText(effective.NoAfterburner),
                    Unknown = DetentSettings.KindText(effective.Unknown),
                    effective.Margin,
                    Text = settings == null ? string.Empty : settings.Format()
                },
                Calibration = calibration == null ? null : new
                {
                    calibration.Idle,
                    calibration.Detent,
                    Full = double.IsNaN(calibration.Full) ? (double?)null : calibration.Full,
                    Cutoff = double.IsNaN(calibration.Cutoff) ? (double?)null : calibration.Cutoff,
                    calibration.Source,
                    calibration.When,
                    calibration.Increasing,
                    IdleTravel = Math.Round(calibration.Travel(calibration.Idle), 4),
                    DetentTravel = Math.Round(calibration.Travel(calibration.Detent), 4),
                    Text = calibration.Format()
                },
                GatesFile = EngineAxisDetents.GatesFile(),
                Groups = groupData,
                Warnings = warnings
            };
        }

        // Game throttle 0..1 over the device travel 0..1 (back end to front end), 51 points.
        private static List<double[]> DetentThrottleSamples(AxisModel model, AxisTuning tuning, DetentCalibration calibration)
        {
            double sigma;
            AxisTuning probe = tuning;
            DetentMath.BaseThrottle(model, probe, calibration, out sigma);
            if (sigma == 0)
                sigma = 1;
            List<double[]> points = new List<double[]>();
            for (int i = 0; i <= 50; i++)
            {
                double travel = i / 50.0;
                double raw = calibration.Increasing ? travel : 1.0 - travel;
                double g = (1.0 + sigma * model.Evaluate(tuning, 2.0 * raw - 1.0)) / 2.0;
                points.Add(new[] { Math.Round(travel, 4), Math.Round(g, 4) });
            }
            return points;
        }

        private static int DetentShow(string relationName, Relation relation, Bind bind, string outPath, bool textOutput, string message = null)
        {
            object data = DetentShowData(relationName, relation, bind, message);
            if (!textOutput)
            {
                EmitPayload(EngineSerialization.ToJson(data), outPath);
                return 0;
            }
            DetentSettings settings = EngineAxisDetents.SettingsOf(bind);
            DetentCalibration calibration = EngineAxisDetents.CalibrationFor(bind);
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(message))
                sb.AppendLine(message);
            sb.AppendLine("Detent mode: " + relationName + (settings == null ? " (off, never set)" : settings.On ? " (on)" : " (off)"));
            if (bind != null)
                sb.AppendLine("Device axis: " + DetentDeviceLabel(bind));
            sb.AppendLine(calibration == null ? "Calibration: none" : "Calibration: idle " + EngineAxisDetents.Pct(calibration.Travel(calibration.Idle)) + ", detent " +
                EngineAxisDetents.Pct(calibration.Travel(calibration.Detent)) + " of the travel (" + calibration.Format() + ")");
            DetentSettings effective = settings ?? new DetentSettings { On = false };
            sb.AppendLine("No afterburner: " + (effective.NoAfterburner == DetentTargetKind.FullAtDetent ? "full power at the detent" : "linear") +
                "; unknown aircraft: " + (effective.Unknown == DetentTargetKind.FullAtDetent ? "full power at the detent" : "linear") + "; margin " + DetentText.Num(effective.Margin));
            foreach (KeyValuePair<string, string> t in EngineAxisDetents.RelationTargets(relation)
                .GroupBy(t => { string s; ThrottleGate g; return EngineAxisDetents.TargetFor(effective, t.Key, t.Value, out s, out g).Format(); })
                .SelectMany(g => g.Select(x => x)).ToList())
            {
                string source;
                ThrottleGate gate;
                DetentTarget target = EngineAxisDetents.TargetFor(effective, t.Key, t.Value, out source, out gate);
                ResolvedTuning resolved = BindTuning.Resolve(bind, t.Key, EngineAxisDetents.HasPlaneCurves(t.Key) ? t.Value : null);
                sb.AppendLine("  " + t.Key + " / " + DetentPlaneTitle(t.Key, t.Value, gate) + ": " + target.Describe() + " (" + source +
                    (gate != null && gate.Confidence.Length > 0 ? ", " + gate.Confidence : string.Empty) + ") -> " + resolved.Source +
                    (resolved.Note.Length > 0 ? " [" + resolved.Note + "]" : string.Empty));
            }
            foreach (string warning in EngineAxisDetents.Warnings)
                sb.AppendLine("Warning: " + warning);
            EmitPayload(sb.ToString(), outPath);
            return 0;
        }

        // ---- gate data ----------------------------------------------------------------------------------------------

        private static int DetentGates(List<string> args, string outPath, bool textOutput)
        {
            string game = PopOptionValue(args, "--game");
            string plane = PopOptionValue(args, "--plane");
            if (args.Count > 0)
                return Fail("Unknown binds detent gates argument(s): " + string.Join(" ", args));
            List<ThrottleGate> gates = EngineAxisDetents.Gates()
                .Where(g => string.IsNullOrWhiteSpace(game) || string.Equals(g.Game, game.Trim(), StringComparison.OrdinalIgnoreCase))
                .Where(g => string.IsNullOrWhiteSpace(plane) || string.Equals(g.Plane, plane.Trim(), StringComparison.OrdinalIgnoreCase) ||
                            g.Aliases.Any(a => string.Equals(a, plane.Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (!textOutput)
            {
                EmitPayload(EngineSerialization.ToJson(new
                {
                    File = EngineAxisDetents.GatesFile(),
                    Aircraft = gates.Select(g => new
                    {
                        g.Game,
                        g.Plane,
                        g.Title,
                        g.NoAfterburner,
                        Afterburner = double.IsNaN(g.Afterburner) ? (double?)null : g.Afterburner,
                        g.IdleGate,
                        g.Confidence,
                        g.Source,
                        g.Notes,
                        g.GameOption
                    }).ToList()
                }), outPath);
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Throttle gates (" + EngineAxisDetents.GatesFile() + "): " + gates.Count.ToString(CultureInfo.InvariantCulture));
            foreach (ThrottleGate g in gates)
            {
                sb.AppendLine("  " + g.Game + " / " + g.Plane + " (" + g.Title + "): " +
                    (g.NoAfterburner ? "no afterburner" : double.IsNaN(g.Afterburner) ? "afterburner start unknown" : "afterburner at " + EngineAxisDetents.Pct(g.Afterburner)) +
                    (g.IdleGate > 0 ? ", idle at " + EngineAxisDetents.Pct(g.IdleGate) : string.Empty) +
                    (g.Confidence.Length > 0 ? " [" + g.Confidence + "]" : string.Empty) + (g.Source.Length > 0 ? " - " + g.Source : string.Empty));
            }
            EmitPayload(sb.ToString(), outPath);
            return 0;
        }
    }
}
