using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.DeviceReference;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine
{
    // One aircraft's throttle gates (DB\Detents\throttle-gates.json): where its afterburner starts on the game's throttle
    // axis (0 idle .. 1 full), an idle cut-off on the axis, the source and how sure it is.
    internal sealed class ThrottleGate
    {
        public string Game = string.Empty;
        // DCS: the Config\Input folder name (F-16C_50); "*" = every aircraft of the game.
        public string Plane = string.Empty;
        public string Title = string.Empty;
        public bool NoAfterburner;
        public double Afterburner = double.NaN;
        public double IdleGate;
        public string Confidence = string.Empty;
        public string Source = string.Empty;
        public string Notes = string.Empty;
        public string GameOption = string.Empty;
        public List<string> Aliases = new List<string>();

        public DetentTarget Target(DetentSettings settings)
        {
            if (NoAfterburner)
            {
                DetentTarget t = settings.NoAfterburner == DetentTargetKind.FullAtDetent ? DetentTarget.Full : DetentTarget.Linear;
                return IdleGate > 0 && t.Kind == DetentTargetKind.FullAtDetent ? new DetentTarget { Kind = t.Kind, IdleGate = IdleGate } : t;
            }
            if (double.IsNaN(Afterburner))
                return null;
            return DetentTarget.Afterburner(Afterburner, IdleGate);
        }
    }

    // Detent mode in the engine (docs/axis-curves.md "Detent mode"): the gate data, the calibration of each device axis
    // (InternalDataManagement.AxisCalibrations) and what a game / aircraft gets (BindTuning.Resolve calls Resolve when no
    // scope of the relation has own values).
    internal static class EngineAxisDetents
    {
        public const string DbFolder = "Detents";
        public const string GatesFileName = "throttle-gates.json";

        private static List<ThrottleGate> gates;
        private static string gatesPath = string.Empty;
        private static readonly Dictionary<string, DetentFit> FitCache = new Dictionary<string, DetentFit>(StringComparer.Ordinal);

        // Warnings of the current run (games that cannot express a curve), for the export summaries.
        public static readonly SortedSet<string> Warnings = new SortedSet<string>(StringComparer.Ordinal);

        public static string GatesFile()
        {
            string overridePath = Environment.GetEnvironmentVariable("JOYPRO_DETENT_GATES");
            return string.IsNullOrWhiteSpace(overridePath) ? GameDataPaths.DbFile(DbFolder, GatesFileName) : overridePath;
        }

        public static List<ThrottleGate> Gates()
        {
            string path = GatesFile();
            if (gates != null && string.Equals(path, gatesPath, StringComparison.OrdinalIgnoreCase))
                return gates;
            gates = LoadGates(path);
            gatesPath = path;
            return gates;
        }

        public static List<ThrottleGate> LoadGates(string path)
        {
            List<ThrottleGate> list = new List<ThrottleGate>();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return list;
            Dictionary<string, object> root;
            try
            {
                root = MiniJson.Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                JoyPro.Logging.JoyProLog.Warn("detent", "Throttle gate data unreadable: " + path + ": " + ex.Message);
                return list;
            }
            object items;
            if (root == null || !root.TryGetValue("aircraft", out items) || !(items is List<object>))
                return list;
            foreach (object item in (List<object>)items)
            {
                Dictionary<string, object> o = item as Dictionary<string, object>;
                if (o == null)
                    continue;
                ThrottleGate g = new ThrottleGate
                {
                    Game = Str(o, "game"),
                    Plane = Str(o, "plane"),
                    Title = Str(o, "title"),
                    NoAfterburner = o.ContainsKey("noAfterburner") && o["noAfterburner"] is bool && (bool)o["noAfterburner"],
                    Afterburner = Num(o, "afterburner"),
                    IdleGate = double.IsNaN(Num(o, "idleGate")) ? 0.0 : Num(o, "idleGate"),
                    Confidence = Str(o, "confidence"),
                    Source = Str(o, "source"),
                    Notes = Str(o, "notes"),
                    GameOption = Str(o, "gameOption")
                };
                object aliases;
                if (o.TryGetValue("aliases", out aliases) && aliases is List<object>)
                    g.Aliases = ((List<object>)aliases).OfType<string>().ToList();
                if (g.Game.Length > 0 && g.Plane.Length > 0)
                    list.Add(g);
            }
            return list;
        }

        private static string Str(Dictionary<string, object> o, string key)
        {
            object v;
            return o.TryGetValue(key, out v) && v is string ? ((string)v).Trim() : string.Empty;
        }

        private static double Num(Dictionary<string, object> o, string key)
        {
            object v;
            return o.TryGetValue(key, out v) && v is double ? (double)v : double.NaN;
        }

        // The aircraft's entry: exact, then the DCS "_easy" variant's base name, an alias, then the game's "*" entry.
        public static ThrottleGate FindGate(string game, string plane)
        {
            List<ThrottleGate> all = Gates();
            string p = (plane ?? string.Empty).Trim();
            Func<ThrottleGate, bool> sameGame = g => string.Equals(g.Game, game ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            ThrottleGate found = all.FirstOrDefault(g => sameGame(g) && string.Equals(g.Plane, p, StringComparison.OrdinalIgnoreCase));
            if (found == null && p.EndsWith("_easy", StringComparison.OrdinalIgnoreCase))
            {
                string basePlane = p.Substring(0, p.Length - 5);
                found = all.FirstOrDefault(g => sameGame(g) && string.Equals(g.Plane, basePlane, StringComparison.OrdinalIgnoreCase));
            }
            if (found == null)
                found = all.FirstOrDefault(g => sameGame(g) && g.Aliases.Any(a => string.Equals(a, p, StringComparison.OrdinalIgnoreCase)));
            if (found == null)
                found = all.FirstOrDefault(g => sameGame(g) && g.Plane == "*");
            return found;
        }

        public static string DeviceKey(Bind bind)
        {
            return bind == null ? string.Empty : DeviceKey(bind.Joystick, bind.JAxis);
        }

        public static string DeviceKey(string device, string axis)
        {
            return (device ?? string.Empty).Trim() + "|" + (axis ?? string.Empty).Trim();
        }

        public static DetentCalibration CalibrationFor(Bind bind)
        {
            string text;
            Dictionary<string, string> store = InternalDataManagement.AxisCalibrations;
            if (bind == null || store == null || !store.TryGetValue(DeviceKey(bind), out text))
                return null;
            return DetentCalibration.Parse(text);
        }

        public static void SetCalibration(Bind bind, DetentCalibration calibration)
        {
            if (InternalDataManagement.AxisCalibrations == null)
                InternalDataManagement.AxisCalibrations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (calibration == null)
                InternalDataManagement.AxisCalibrations.Remove(DeviceKey(bind));
            else
                InternalDataManagement.AxisCalibrations[DeviceKey(bind)] = calibration.Format();
            FitCache.Clear();
        }

        public static DetentSettings SettingsOf(Bind bind)
        {
            string text;
            if (bind == null || bind.TuningOverrides == null || !bind.TuningOverrides.TryGetValue(DetentSettings.Key, out text))
                return null;
            return DetentSettings.Parse(text);
        }

        public static void SetSettings(Bind bind, DetentSettings settings)
        {
            if (settings == null)
            {
                if (bind.TuningOverrides != null)
                {
                    bind.TuningOverrides.Remove(DetentSettings.Key);
                    if (bind.TuningOverrides.Count == 0)
                        bind.TuningOverrides = null;
                }
            }
            else
            {
                if (bind.TuningOverrides == null)
                    bind.TuningOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bind.TuningOverrides[DetentSettings.Key] = settings.Format();
            }
            FitCache.Clear();
        }

        // What an aircraft gets and where that comes from: "relation" (its own value), "game data", "unknown".
        public static DetentTarget TargetFor(DetentSettings settings, string game, string plane, out string source, out ThrottleGate gate)
        {
            gate = FindGate(game, plane);
            DetentTarget own = settings.OwnTarget(game, plane);
            if (own != null)
            {
                source = "relation";
                return own;
            }
            DetentTarget data = gate == null ? null : gate.Target(settings);
            if (data != null)
            {
                source = "game data";
                return data;
            }
            source = "unknown";
            return settings.Unknown == DetentTargetKind.FullAtDetent ? DetentTarget.Full : DetentTarget.Linear;
        }

        // Every (game, aircraft) the relation flies, in node order.
        public static List<KeyValuePair<string, string>> RelationTargets(Relation relation)
        {
            List<KeyValuePair<string, string>> targets = new List<KeyValuePair<string, string>>();
            if (relation == null || relation.NODES == null)
                return targets;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RelationItem node in relation.NODES)
            {
                if (node == null)
                    continue;
                string game = string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game.Trim();
                List<string> planes = node.GetActiveAircraftList();
                if (planes.Count == 0 && !string.Equals(game, "DCS", StringComparison.OrdinalIgnoreCase))
                    planes.Add(game);
                foreach (string plane in planes)
                {
                    if (seen.Add(game + "\n" + plane))
                        targets.Add(new KeyValuePair<string, string>(game, plane));
                }
            }
            return targets;
        }

        public static bool HasPlaneCurves(string game)
        {
            return string.Equals(game, "DCS", StringComparison.OrdinalIgnoreCase);
        }

        private static Relation RelationOf(Bind bind)
        {
            if (bind == null)
                return null;
            if (bind.Rl != null)
                return bind.Rl;
            foreach (KeyValuePair<string, Bind> entry in InternalDataManagement.AllBinds)
            {
                Relation relation;
                if (ReferenceEquals(entry.Value, bind) && InternalDataManagement.AllRelations.TryGetValue(entry.Key, out relation))
                    return relation;
            }
            return null;
        }

        // A game with one curve for all its aircraft: the target most of the relation's aircraft of that game need.
        public static DetentTarget GameTarget(DetentSettings settings, Relation relation, string game, out List<string> differing)
        {
            differing = new List<string>();
            List<KeyValuePair<string, DetentTarget>> found = new List<KeyValuePair<string, DetentTarget>>();
            foreach (KeyValuePair<string, string> target in RelationTargets(relation).Where(t => string.Equals(t.Key, game, StringComparison.OrdinalIgnoreCase)))
            {
                string source;
                ThrottleGate gate;
                found.Add(new KeyValuePair<string, DetentTarget>(target.Value, TargetFor(settings, game, target.Value, out source, out gate)));
            }
            if (found.Count == 0)
            {
                string source;
                ThrottleGate gate;
                return TargetFor(settings, game, game, out source, out gate);
            }
            DetentTarget best = found.GroupBy(f => f.Value.Format()).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().First().Value;
            differing = found.Where(f => !f.Value.SameAs(best)).Select(f => f.Key).ToList();
            return best;
        }

        // BindTuning.Resolve's detent step: null when the relation has no detent mode, the device axis is not
        // calibrated, the aircraft keeps the relation's curve, or the game cannot express the curve (then a warning).
        public static ResolvedTuning Resolve(Bind bind, string game, string plane, ResolvedTuning normal)
        {
            DetentSettings settings = SettingsOf(bind);
            if (settings == null || !settings.On || normal == null || normal.Model == null)
                return null;
            DetentCalibration calibration = CalibrationFor(bind);
            if (calibration == null || calibration.Validate().Length > 0)
                return null;
            DetentTarget target;
            string note;
            if (!string.IsNullOrWhiteSpace(plane) && HasPlaneCurves(game))
            {
                string source;
                ThrottleGate gate;
                target = TargetFor(settings, game, plane, out source, out gate);
                note = target.Describe() + " (" + source + ")";
            }
            else
            {
                List<string> differing;
                target = GameTarget(settings, RelationOf(bind), game, out differing);
                note = target.Describe() + (differing.Count > 0 ? "; " + game + " has one curve for all aircraft: " + string.Join(", ", differing) + " would need another" : string.Empty);
            }
            if (target.Kind == DetentTargetKind.Linear)
                return null;
            DetentFit fit = FitFor(normal.Model, normal.Tuning, calibration, target, settings.Margin);
            if (fit.Warning.Length > 0)
                Warnings.Add("Detent mode, " + game + (string.IsNullOrWhiteSpace(plane) ? string.Empty : " / " + plane) + ": " + fit.Warning);
            if (fit.Fallback || fit.Tuning == null)
                return null;
            return new ResolvedTuning
            {
                Tuning = fit.Tuning.Clone(),
                Model = normal.Model,
                Source = BindTuning.DetentSource,
                Fitted = true,
                Note = note + "; " + fit.Method + ", off by up to " + Pct(fit.MaxError)
            };
        }

        public static DetentFit FitFor(AxisModel model, AxisTuning baseTuning, DetentCalibration calibration, DetentTarget target, double margin)
        {
            string key = model.Id + "\n" + baseTuning.Format() + "\n" + calibration.Format() + "\n" + target.Format() + "\n" + margin.ToString("R", CultureInfo.InvariantCulture);
            DetentFit fit;
            if (FitCache.TryGetValue(key, out fit))
                return fit;
            fit = DetentMath.Fit(model, baseTuning, calibration, target, margin);
            if (FitCache.Count > 2000)
                FitCache.Clear();
            FitCache[key] = fit;
            return fit;
        }

        public static string Pct(double fraction)
        {
            return (fraction * 100.0).ToString(fraction < 0.001 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + " %";
        }
    }
}
