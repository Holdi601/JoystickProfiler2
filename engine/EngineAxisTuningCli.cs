using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine
{
    // engine binds tuning ...: the axis curve manager on the command line (docs/axis-curves.md).
    //   binds tuning models                                   response models and their parameters
    //   binds tuning show <relation> [--game G] [--plane P]   every scope of the bind; with --game, what G (P) uses;
    //                                                         always: per game / DCS aircraft of the relation the scope it uses
    //   binds tuning set <relation> --scope S [--scope S2 ...] [options]   base | game:G | plane:G:P | family:F; several
    //                                                         scopes get the same options in one run (one history version);
    //                                                         --clear-scope S3: also removes S3's own tuning in that run
    //                                                         (--curve-points x:y,x:y,...|none: a curve with any number of stops)
    //   binds tuning clear <relation> --scope S [--scope S2 ...]
    //   binds tuning preview <relation> --game G [--plane P] [--points N]
    internal static partial class Program
    {
        private static int RunBindTuning(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count > 1 ? command[1].ToLowerInvariant() : "models";
            List<string> args = command.Skip(2).ToList();
            switch (action)
            {
                case "models":
                    return EmitTuningModels(outPath, textOutput);
                case "show":
                case "preview":
                case "set":
                case "clear":
                    break;
                default:
                    return Fail("binds tuning: unknown action '" + action + "' (models, show, set, clear, preview).");
            }
            if (args.Count < 1)
                return Fail("binds tuning " + action + " requires a relation or bind name.");

            string query = args[0];
            args.RemoveAt(0);
            List<string> scopeOptions;
            List<string> clearScopeOptions;
            string game;
            string plane;
            string pointsRaw;
            List<string> paramOptions;
            bool inverted;
            bool invertedProvided;
            bool slider;
            bool sliderProvided;
            string deadzoneRaw;
            string satXRaw;
            string satYRaw;
            List<string> curvatureOptions;
            string curvePointsRaw;
            string bindNumberRaw;
            try
            {
                // --bind <n>: input n of the relation (experimental multi-bind; 1 = the main bind, the default).
                bindNumberRaw = PopOptionValue(args, "--bind");
                curvePointsRaw = PopOptionValue(args, "--curve-points");
                scopeOptions = PopOptionValues(args, "--scope");
                clearScopeOptions = PopOptionValues(args, "--clear-scope");
                game = PopOptionValue(args, "--game");
                plane = PopOptionValue(args, "--plane");
                pointsRaw = PopOptionValue(args, "--points");
                paramOptions = PopOptionValues(args, "--param");
                inverted = PopOptionBoolean(args, "--inverted", false, out invertedProvided);
                slider = PopOptionBoolean(args, "--slider", false, out sliderProvided);
                deadzoneRaw = PopOptionValue(args, "--deadzone");
                satXRaw = PopOptionValue(args, "--saturation-x");
                satYRaw = PopOptionValue(args, "--saturation-y");
                curvatureOptions = PopOptionValues(args, "--curvature");
            }
            catch (ArgumentException ex)
            {
                return Fail(ex.Message);
            }
            if (args.Count > 0)
                return Fail("Unknown binds tuning argument(s): " + string.Join(" ", args));

            List<string> candidates;
            string relationName;
            if (!TryResolveRelationOrBindName(query, out relationName, out candidates))
                return FailResolution("relation/bind", query, candidates);
            Relation relation;
            if (!InternalDataManagement.AllRelations.TryGetValue(relationName, out relation) || relation == null)
                return Fail("Relation not found: " + relationName);
            if (!relation.ISAXIS)
                return Fail("Axis tuning is only for axis relations; '" + relationName + "' is a button relation.");
            Bind bind;
            InternalDataManagement.AllBinds.TryGetValue(relationName, out bind);
            if (!string.IsNullOrWhiteSpace(bindNumberRaw) && bindNumberRaw.Trim() != "1")
            {
                int bindIndex;
                string bindError;
                if (!TryExtraIndex(relation, bindNumberRaw, out bindIndex, out bindError))
                    return Fail(bindError);
                bind = MultiBind.Extras(relation)[bindIndex];
            }

            if (action == "show")
                return EmitTuningShow(relationName, relation, bind, game, plane, outPath, textOutput);
            if (action == "preview")
            {
                if (string.IsNullOrWhiteSpace(game))
                    return Fail("binds tuning preview requires --game.");
                int points = 21;
                if (!string.IsNullOrWhiteSpace(pointsRaw) && (!int.TryParse(pointsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out points) || points < 2 || points > 1001))
                    return Fail("--points must be 2..1001.");
                return EmitTuningPreview(relationName, bind, game, plane, points, outPath, textOutput);
            }

            // One or more scopes (--scope repeated): the same change for each, in this one run, so one undo takes it back.
            List<string> scopes = new List<string>();
            foreach (string raw in scopeOptions)
            {
                string one = (raw ?? string.Empty).Trim();
                if (one.Length == 0)
                    continue;
                if (!BindTuning.IsValidScope(one))
                    return Fail("Invalid scope '" + one + "'. Use base, game:<game>, plane:<game>:<plane> or family:<family>.");
                if (!scopes.Contains(one, StringComparer.OrdinalIgnoreCase))
                    scopes.Add(one);
            }
            // set --clear-scope: scopes that lose their own tuning in the same run (e.g. aircraft that follow the game's
            // curve again when it is set for all of them).
            List<string> clearScopes = new List<string>();
            foreach (string raw in clearScopeOptions)
            {
                string one = (raw ?? string.Empty).Trim();
                if (one.Length == 0)
                    continue;
                if (action != "set")
                    return Fail("--clear-scope is an option of binds tuning set; binds tuning clear takes --scope.");
                if (!BindTuning.IsValidScope(one) || one == BindTuning.BaseScope)
                    return Fail("Invalid --clear-scope '" + one + "'. Use game:<game>, plane:<game>:<plane> or family:<family> (the base cannot be cleared).");
                if (scopes.Contains(one, StringComparer.OrdinalIgnoreCase))
                    return Fail("'" + one + "' is both set (--scope) and cleared (--clear-scope).");
                if (!clearScopes.Contains(one, StringComparer.OrdinalIgnoreCase))
                    clearScopes.Add(one);
            }
            if (scopes.Count == 0)
                return Fail("binds tuning " + action + " requires --scope (base, game:<game>, plane:<game>:<plane>, family:<family>; repeat it for several).");
            if (bind == null)
                return Fail("Relation '" + relationName + "' has no bind; bind an axis first.");

            if (action == "clear")
            {
                if (scopes.Contains(BindTuning.BaseScope))
                    return Fail("The base tuning cannot be cleared; set its values instead.");
                List<string> cleared = scopes.Where(s => BindTuning.Clear(bind, s)).ToList();
                List<string> without = scopes.Where(s => !cleared.Contains(s)).ToList();
                List<string> lines = new List<string>();
                if (cleared.Count > 0)
                    lines.Add("Cleared " + string.Join(", ", cleared) + ".");
                if (without.Count > 0)
                    lines.Add("No tuning for " + string.Join(", ", without) + ".");
                return EmitTuningResult(relationName, string.Join("\n", lines), outPath, textOutput);
            }

            // set: each scope starts from its own tuning, else from what it resolves to now; nothing is stored until the
            // options are valid for every scope.
            List<KeyValuePair<string, AxisTuning>> results = new List<KeyValuePair<string, AxisTuning>>();
            foreach (string scope in scopes)
            {
                AxisTuning start = BindTuning.Get(bind, scope);
                if (start == null)
                {
                    string[] parts = scope.Split(':');
                    start = parts[0] == "game" ? BindTuning.Resolve(bind, parts[1], null).Tuning
                        : parts[0] == "plane" ? BindTuning.Resolve(bind, parts[1], string.Join(":", parts.Skip(2))).Tuning
                        : BindTuning.Base(bind);
                }
                string error = ApplyTuningOptions(start, scope, invertedProvided, inverted, sliderProvided, slider, deadzoneRaw, satXRaw, satYRaw,
                    curvatureOptions, paramOptions, curvePointsRaw);
                if (error != null)
                    return Fail(error);
                results.Add(new KeyValuePair<string, AxisTuning>(scope, start));
            }
            foreach (KeyValuePair<string, AxisTuning> result in results)
                BindTuning.Set(bind, result.Key, result.Value);
            List<string> clearedToo = clearScopes.Where(c => BindTuning.Clear(bind, c)).ToList();
            string conflicts = SharedAxisConflicts(relationName, InternalDataManagement.AllRelations[relationName], bind);
            StringBuilder message = new StringBuilder();
            foreach (KeyValuePair<string, AxisTuning> result in results)
            {
                if (message.Length > 0)
                    message.Append("\n");
                message.Append("Set " + result.Key + ": " + (result.Key == BindTuning.BaseScope ? BindTuning.Base(bind).Format() : result.Value.Format()));
            }
            if (clearedToo.Count > 0)
                message.Append("\nCleared " + string.Join(", ", clearedToo) + ".");
            if (scopes.Contains(BindTuning.BaseScope))
            {
                // The base scope keeps only invert, slider, dead zone, saturation and curvature (the bind itself): model
                // parameters and control points are not stored there, so the message shows what was stored and says so.
                List<string> notStored = new List<string>();
                if (paramOptions.Count > 0)
                    notStored.Add("--param");
                if (!string.IsNullOrWhiteSpace(curvePointsRaw) && !string.Equals(curvePointsRaw.Trim(), "none", StringComparison.OrdinalIgnoreCase))
                    notStored.Add("--curve-points");
                if (notStored.Count > 0)
                    message.Append("\nNot set: " + string.Join(" and ", notStored) + (notStored.Count == 1 ? " does" : " do") + " not apply to the base scope (it keeps invert, slider, dead zone, " +
                      "saturation and curvature); set " + (notStored.Count == 1 ? "it" : "them") + " on a game, plane or family scope, e.g. --scope game:<game>.");
            }
            return EmitTuningResult(relationName, message + conflicts, outPath, textOutput);
        }

        // The options of binds tuning set applied to one scope's tuning; an error text when one is not valid there.
        private static string ApplyTuningOptions(AxisTuning tuning, string scope, bool invertedProvided, bool inverted, bool sliderProvided, bool slider,
            string deadzoneRaw, string satXRaw, string satYRaw, List<string> curvatureOptions, List<string> paramOptions, string curvePointsRaw)
        {
            AxisTuning before = tuning.Clone();
            double number;
            if (invertedProvided)
                tuning.Inverted = inverted;
            if (sliderProvided)
                tuning.Slider = slider;
            if (!string.IsNullOrWhiteSpace(deadzoneRaw))
            {
                if (!TryParseUnit(deadzoneRaw, out number))
                    return "--deadzone must be 0..1.";
                tuning.Deadzone = number;
            }
            if (!string.IsNullOrWhiteSpace(satXRaw))
            {
                if (!TryParseUnit(satXRaw, out number))
                    return "--saturation-x must be 0..1.";
                tuning.SaturationX = number;
            }
            if (!string.IsNullOrWhiteSpace(satYRaw))
            {
                if (!TryParseUnit(satYRaw, out number))
                    return "--saturation-y must be 0..1.";
                tuning.SaturationY = number;
            }
            if (curvatureOptions.Count > 0)
            {
                List<double> curve = new List<double>();
                foreach (string item in string.Join(",", curvatureOptions).Split(','))
                {
                    if (!double.TryParse(item.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                        return "--curvature takes numbers (one value, or 11 user-curve points).";
                    curve.Add(number);
                }
                if (curve.Count != 1 && curve.Count != AxisModels.UserCurvePoints)
                    return "--curvature takes one value or 11 user-curve points.";
                if (curve.Count == 1 && (curve[0] < -1 || curve[0] > 1))
                    return "A single curvature must be -1..1.";
                if (curve.Count == AxisModels.UserCurvePoints && curve.Any(c => c < 0 || c > 1))
                    return "User-curve points must be 0..1.";
                tuning.Curvature = curve;
            }
            foreach (string option in paramOptions)
            {
                int eq = option.IndexOf('=');
                if (eq <= 0 || !double.TryParse(option.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    return "--param takes name=value, e.g. --param sensitivity=22.";
                string key = option.Substring(0, eq).Trim();
                // The scope's own model first: several models share a key name with other ranges ("sensitivity").
                AxisModel scopeModel = BindTuning.ModelForScope(scope);
                AxisParameter parameter = (scopeModel == null ? Enumerable.Empty<AxisParameter>() : scopeModel.Parameters)
                    .Concat(AxisModels.All.SelectMany(m => m.Parameters))
                    .FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase) &&
                                         (p.Kind == AxisParameterKind.Number || (p.Kind == AxisParameterKind.Toggle && p.Key != "invert" && p.Key != "slider")));
                if (parameter == null)
                    return "Unknown model parameter '" + key + "'. See: engine binds tuning models.";
                if (parameter.Kind == AxisParameterKind.Toggle)
                {
                    if (number != 0 && number != 1)
                        return key + " is a switch: 0 or 1.";
                }
                else if (number < parameter.Min || number > parameter.Max)
                    return key + " must be " + AxisTuning.Num(parameter.Min) + ".." + AxisTuning.Num(parameter.Max) + ".";
                tuning.Native[parameter.Key] = number;
            }
            // Control points (a game curve with any number of stops, e.g. X-Plane): "x:y,x:y,..." or "none". A curve
            // with control points keeps them until dead zone, saturation or curvature change; then those define it.
            if (!string.IsNullOrWhiteSpace(curvePointsRaw))
            {
                if (string.Equals(curvePointsRaw.Trim(), "none", StringComparison.OrdinalIgnoreCase))
                    tuning.ClearControlPoints();
                else
                {
                    List<KeyValuePair<double, double>> points = new List<KeyValuePair<double, double>>();
                    foreach (string item in curvePointsRaw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] xy = item.Split(':');
                        double x;
                        double y;
                        if (xy.Length != 2 || !TryParseUnit(xy[0].Trim(), out x) || !TryParseUnit(xy[1].Trim(), out y))
                            return "--curve-points takes x:y pairs in 0..1, e.g. 0:0,0.4:0.3,1:1 (or none).";
                        if (points.Count > 0 && x <= points[points.Count - 1].Key)
                            return "--curve-points: x must increase from point to point.";
                        points.Add(new KeyValuePair<double, double>(x, y));
                    }
                    if (points.Count < 2)
                        return "--curve-points needs at least two points.";
                    tuning.SetControlPoints(points);
                }
            }
            else if (tuning.HasControlPoints && CurveFieldsChanged(before, tuning))
                tuning.ClearControlPoints();
            return null;
        }

        // Games that keep some axis values once per physical device axis (MSFS's Sensitivity page): other relations bound
        // to the same device axis for that game with different values (the export writes one set and warns again).
        private static string SharedAxisConflicts(string relationName, Relation relation, Bind bind)
        {
            if (relation == null || relation.NODES == null || bind == null || string.IsNullOrWhiteSpace(bind.Joystick) || string.IsNullOrWhiteSpace(bind.JAxis))
                return string.Empty;
            StringBuilder sb = new StringBuilder();
            foreach (JoyPro.Engine.Games.IGameIntegration game in JoyPro.Engine.Games.GameRegistry.All)
            {
                AxisModel model = AxisModels.ForGame(game.GameId);
                if (model.SharedPerAxisKeys == null || model.SharedPerAxisKeys.Count == 0 ||
                    !relation.NODES.Any(n => n != null && JoyPro.Engine.Games.GameRegistry.GameMatches(game, n.Game)))
                    continue;
                AxisTuning mine = BindTuning.Resolve(bind, game.GameId, null).Tuning;
                if (model.PerActionKey != null && mine.NativeOr(model.PerActionKey, 0) >= 0.5)
                    continue;
                foreach (KeyValuePair<string, Relation> other in InternalDataManagement.AllRelations.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
                {
                    Bind otherBind;
                    if (string.Equals(other.Key, relationName, StringComparison.OrdinalIgnoreCase) || other.Value == null || !other.Value.ISAXIS || other.Value.NODES == null ||
                        !InternalDataManagement.AllBinds.TryGetValue(other.Key, out otherBind) || otherBind == null ||
                        !string.Equals((otherBind.Joystick ?? string.Empty).Trim(), bind.Joystick.Trim(), StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals((otherBind.JAxis ?? string.Empty).Trim(), bind.JAxis.Trim(), StringComparison.OrdinalIgnoreCase) ||
                        !other.Value.NODES.Any(n => n != null && JoyPro.Engine.Games.GameRegistry.GameMatches(game, n.Game)))
                        continue;
                    AxisTuning theirs = BindTuning.Resolve(otherBind, game.GameId, null).Tuning;
                    if (model.PerActionKey != null && theirs.NativeOr(model.PerActionKey, 0) >= 0.5)
                        continue;
                    List<string> differ = model.SharedPerAxisKeys.Where(k =>
                    {
                        AxisParameter p = model.Parameters.FirstOrDefault(x => x.Key == k);
                        double fallback = p == null ? 0 : p.Default;
                        return Math.Abs(mine.NativeOr(k, fallback) - theirs.NativeOr(k, fallback)) > 1e-9;
                    }).ToList();
                    if (differ.Count > 0)
                        sb.Append("\nWarning: ").Append(game.DisplayName).Append(" keeps ").Append(string.Join(", ", differ)).Append(" once per device axis; relation '")
                          .Append(other.Key).Append("' on the same axis (").Append(bind.JAxis.Trim()).Append(") has other values. Give both the same values")
                          .Append(model.PerActionKey != null ? " or set " + model.PerActionKey + "=1 on one of them." : ".");
                }
            }
            return sb.ToString();
        }

        private static bool CurveFieldsChanged(AxisTuning a, AxisTuning b)
        {
            List<double> ca = a.Curvature ?? new List<double> { 0.0 };
            List<double> cb = b.Curvature ?? new List<double> { 0.0 };
            return Math.Abs(a.Deadzone - b.Deadzone) > 1e-9 || Math.Abs(a.SaturationX - b.SaturationX) > 1e-9 || Math.Abs(a.SaturationY - b.SaturationY) > 1e-9 ||
                   ca.Count != cb.Count || ca.Zip(cb, (x, y) => Math.Abs(x - y)).Any(d => d > 1e-6);
        }

        private static bool TryParseUnit(string raw, out double value)
        {
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0 && value <= 1;
        }

        private static int EmitTuningModels(string outPath, bool textOutput)
        {
            if (!textOutput)
            {
                EmitPayload(EngineSerialization.ToJson(new
                {
                    Models = AxisModels.All.Select(m => new
                    {
                        m.Id,
                        m.Title,
                        m.Description,
                        m.Units,
                        CurvePoints = m.CurveStops != null,
                        Parameters = m.Parameters.Select(p => new { p.Key, p.Label, Kind = p.Kind.ToString(), p.Min, p.Max, p.Step, p.Default }).ToList()
                    }).ToList()
                }), outPath);
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            foreach (AxisModel model in AxisModels.All)
            {
                sb.AppendLine(model.Id + " - " + model.Title);
                sb.AppendLine("  " + model.Description);
                sb.AppendLine("  Units: " + model.Units);
                foreach (AxisParameter p in model.Parameters)
                    sb.AppendLine("  - " + p.Key + " (" + p.Label + ", " + p.Kind + (p.Kind == AxisParameterKind.Number ? " " + AxisTuning.Num(p.Min) + ".." + AxisTuning.Num(p.Max) : string.Empty) + ")");
                if (model.CurveStops != null)
                    sb.AppendLine("  - curve points (--curve-points x:y,x:y,...|none: free stops in 0..1, x increasing)");
            }
            EmitPayload(sb.ToString(), outPath);
            return 0;
        }

        // Per game of the relation (DCS: per aircraft, the only game whose curves are written per aircraft) the scope its
        // curve comes from, before detent mode; Detent: detent mode bends it for that aircraft. The GUI's "Applies to" list
        // shows it ("own curve", "uses DCS").
        internal sealed class TuningUse
        {
            public string Game;
            public string Plane;
            public string Source;
            public bool Fitted;
            public bool Detent;
        }

        internal static List<TuningUse> TuningUses(Relation relation, Bind bind)
        {
            List<TuningUse> uses = new List<TuningUse>();
            if (relation == null || relation.NODES == null)
                return uses;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RelationItem node in relation.NODES)
            {
                string game = node == null ? null : (node.Game ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(game))
                    continue;
                IEnumerable<string> planes = EngineAxisDetents.HasPlaneCurves(game) ? node.GetActiveAircraftList() : new List<string> { null };
                foreach (string plane in planes)
                {
                    if (!seen.Add(game + "|" + (plane ?? string.Empty)))
                        continue;
                    ResolvedTuning normal = BindTuning.ResolveWithoutDetent(bind, game, plane);
                    ResolvedTuning used = BindTuning.Resolve(bind, game, plane);
                    uses.Add(new TuningUse { Game = game, Plane = plane, Source = normal.Source, Fitted = normal.Fitted, Detent = used.Source == BindTuning.DetentSource });
                }
            }
            return uses.OrderBy(u => u.Game, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.Plane ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static int EmitTuningShow(string relationName, Relation relation, Bind bind, string game, string plane, string outPath, bool textOutput)
        {
            List<TuningUse> uses = TuningUses(relation, bind);
            Dictionary<string, string> scopes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            scopes[BindTuning.BaseScope] = BindTuning.Base(bind).FormatExact();
            if (bind != null && bind.TuningOverrides != null)
            {
                foreach (KeyValuePair<string, string> entry in bind.TuningOverrides.Where(e => BindTuning.IsTuningScopeKey(e.Key)).OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
                    scopes[entry.Key] = entry.Value;
            }
            ResolvedTuning resolved = string.IsNullOrWhiteSpace(game) ? null : BindTuning.Resolve(bind, game, plane);
            if (!textOutput)
            {
                EmitPayload(EngineSerialization.ToJson(new
                {
                    Relation = relationName,
                    Scopes = scopes,
                    Game = game,
                    Plane = plane,
                    Resolved = resolved == null ? null : new { Model = resolved.Model.Id, resolved.Source, resolved.Fitted, Tuning = resolved.Tuning.Format(), resolved.Note },
                    Uses = uses.Select(u => new { u.Game, u.Plane, u.Source, u.Fitted, u.Detent }).ToList()
                }), outPath);
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Axis tuning: " + relationName);
            foreach (KeyValuePair<string, string> entry in scopes)
                sb.AppendLine("  " + entry.Key + ": " + entry.Value);
            if (resolved != null)
                sb.AppendLine(game + (string.IsNullOrWhiteSpace(plane) ? string.Empty : " / " + plane) + " uses (" + resolved.Model.Id + ", from " + resolved.Source + (resolved.Source == BindTuning.DetentSource ? ", detent mode: " + resolved.Note : resolved.Fitted ? ", fitted to the base curve" : string.Empty) + "): " + resolved.Tuning.Format());
            if (uses.Count > 0)
            {
                sb.AppendLine("Curve per game and aircraft:");
                foreach (TuningUse use in uses)
                    sb.AppendLine("  " + use.Game + (use.Plane == null ? string.Empty : " / " + use.Plane) + ": " +
                                  (use.Source == BindTuning.PlaneScope(use.Game, use.Plane ?? string.Empty) || use.Source == BindTuning.GameScope(use.Game) && use.Plane == null ? "own (" + use.Source + ")" : "from " + use.Source) +
                                  (use.Fitted ? ", fitted" : string.Empty) + (use.Detent ? ", detent mode" : string.Empty));
            }
            EmitPayload(sb.ToString(), outPath);
            return 0;
        }

        private static int EmitTuningPreview(string relationName, Bind bind, string game, string plane, int points, string outPath, bool textOutput)
        {
            ResolvedTuning resolved = BindTuning.Resolve(bind, game, plane);
            List<KeyValuePair<double, double>> samples = AxisModels.Sample(resolved.Model, resolved.Tuning, points);
            if (!textOutput)
            {
                EmitPayload(EngineSerialization.ToJson(new
                {
                    Relation = relationName,
                    Game = game,
                    Plane = plane,
                    Model = resolved.Model.Id,
                    resolved.Source,
                    resolved.Fitted,
                    Tuning = resolved.Tuning.Format(),
                    Points = samples.Select(s => new[] { Math.Round(s.Key, 6), Math.Round(s.Value, 6) }).ToList()
                }), outPath);
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Axis response: " + relationName + " in " + game + (string.IsNullOrWhiteSpace(plane) ? string.Empty : " / " + plane));
            sb.AppendLine("Model: " + resolved.Model.Id + ", from " + resolved.Source + (resolved.Fitted ? " (fitted to the base curve)" : string.Empty));
            sb.AppendLine("Tuning: " + resolved.Tuning.Format());
            foreach (KeyValuePair<double, double> s in samples)
                sb.AppendLine("  " + s.Key.ToString("+0.000;-0.000", CultureInfo.InvariantCulture) + " -> " + s.Value.ToString("+0.000000;-0.000000", CultureInfo.InvariantCulture));
            EmitPayload(sb.ToString(), outPath);
            return 0;
        }

        private static int EmitTuningResult(string relationName, string message, string outPath, bool textOutput)
        {
            EmitPayload(textOutput ? "Axis tuning: " + relationName + "\n" + message : EngineSerialization.ToJson(new { Relation = relationName, Message = message }), outPath);
            return 0;
        }
    }
}
