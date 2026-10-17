#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

// Detent mode for throttles (docs/axis-curves.md "Detent mode"): one throttle relation for all aircraft; per aircraft
// the response is bent so that the device's physical afterburner detent is where that aircraft's afterburner starts.
// Plain math without engine dependencies (C# 7.3): the GUI links this file for its previews and tests.
namespace JoyPro.Engine.Axis
{
    // Where the gates of one device axis are, as raw positions 0..1 (the DirectInput axis from its minimum to its maximum,
    // as the capture service reads it and as the games get it before their curves).
    public sealed class DetentCalibration
    {
        // At idle: on the idle detent when the device has one, else at the back stop.
        public double Idle;
        // At the afterburner detent (where the gate starts).
        public double Detent;
        // Fully forward; NaN: the axis end beyond the detent.
        public double Full = double.NaN;
        // Fully back behind an idle detent (cut-off); NaN: none (idle is the back end of the travel).
        public double Cutoff = double.NaN;
        // wizard | profile (inferred from the profile's curves) | manual
        public string Source = "wizard";
        public string When = string.Empty;

        public bool Increasing
        {
            get { return Detent >= Idle; }
        }

        public double FullOrEnd
        {
            get { return double.IsNaN(Full) ? (Increasing ? 1.0 : 0.0) : Full; }
        }

        public bool HasCutoff
        {
            get { return !double.IsNaN(Cutoff) && Math.Abs(Cutoff - Idle) >= 0.01; }
        }

        public double BackEnd
        {
            get { return HasCutoff ? Cutoff : (Increasing ? 0.0 : 1.0); }
        }

        // Share of the travel from the back end (0) to the front end (1) of a raw position.
        public double Travel(double raw)
        {
            double back = Increasing ? 0.0 : 1.0;
            double span = Increasing ? 1.0 : -1.0;
            return Math.Max(0.0, Math.Min(1.0, (raw - back) / span));
        }

        public string Validate()
        {
            foreach (double v in new[] { Idle, Detent })
            {
                if (double.IsNaN(v) || v < 0 || v > 1)
                    return "Idle and detent must be positions 0..1.";
            }
            if (Math.Abs(Detent - Idle) < 0.02)
                return "The detent must be at least 2 % of the travel away from idle.";
            if (!double.IsNaN(Full))
            {
                if (Full < 0 || Full > 1)
                    return "Full must be a position 0..1.";
                if (Increasing ? Full <= Detent + 0.005 : Full >= Detent - 0.005)
                    return "Full must be beyond the detent.";
            }
            if (!double.IsNaN(Cutoff))
            {
                if (Cutoff < 0 || Cutoff > 1)
                    return "The cut-off must be a position 0..1.";
                if (Increasing ? Cutoff > Idle + 1e-9 : Cutoff < Idle - 1e-9)
                    return "The cut-off must be behind idle.";
            }
            return string.Empty;
        }

        public string Format()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("idle=").Append(Num(Idle)).Append(";det=").Append(Num(Detent));
            if (!double.IsNaN(Full))
                sb.Append(";full=").Append(Num(Full));
            if (!double.IsNaN(Cutoff))
                sb.Append(";cut=").Append(Num(Cutoff));
            if (!string.IsNullOrWhiteSpace(Source))
                sb.Append(";src=").Append(Source.Trim());
            if (!string.IsNullOrWhiteSpace(When))
                sb.Append(";at=").Append(When.Trim());
            return sb.ToString();
        }

        public static DetentCalibration Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            DetentCalibration c = new DetentCalibration { Source = string.Empty };
            bool idle = false;
            bool det = false;
            foreach (KeyValuePair<string, string> part in DetentText.Pairs(text))
            {
                double v;
                switch (part.Key)
                {
                    case "idle": if (DetentText.TryNum(part.Value, out v)) { c.Idle = v; idle = true; } break;
                    case "det": if (DetentText.TryNum(part.Value, out v)) { c.Detent = v; det = true; } break;
                    case "full": if (DetentText.TryNum(part.Value, out v)) c.Full = v; break;
                    case "cut": if (DetentText.TryNum(part.Value, out v)) c.Cutoff = v; break;
                    case "src": c.Source = part.Value; break;
                    case "at": c.When = part.Value; break;
                }
            }
            return idle && det ? c : null;
        }

        private static string Num(double v)
        {
            return Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
        }
    }

    public enum DetentTargetKind
    {
        // The aircraft has an afterburner: the device's detent is where it starts.
        Afterburner,
        // No afterburner: full power (or full collective) at the device's detent, the rest of the travel stays full.
        FullAtDetent,
        // Leave the relation's curve as it is for this aircraft.
        Linear
    }

    // What one aircraft (or game) needs: "ab:0.78" (afterburner from 78 % of the game's throttle), "ab:0.78/idle:0.05"
    // (with an idle cut-off on the axis below 5 %), "full", "linear".
    public sealed class DetentTarget
    {
        public DetentTargetKind Kind;
        // Game throttle fraction 0..1 where the afterburner starts.
        public double AbStart = 1.0;
        // Game throttle fraction of idle for aircraft whose cut-off is on the axis (below idle); 0 = none.
        public double IdleGate;

        public static DetentTarget Afterburner(double start, double idleGate = 0)
        {
            return new DetentTarget { Kind = DetentTargetKind.Afterburner, AbStart = start, IdleGate = idleGate };
        }

        public static readonly DetentTarget Full = new DetentTarget { Kind = DetentTargetKind.FullAtDetent };
        public static readonly DetentTarget Linear = new DetentTarget { Kind = DetentTargetKind.Linear };

        public string Format()
        {
            switch (Kind)
            {
                case DetentTargetKind.FullAtDetent: return IdleGate > 0 ? "full/idle:" + DetentText.Num(IdleGate) : "full";
                case DetentTargetKind.Linear: return "linear";
                default: return "ab:" + DetentText.Num(AbStart) + (IdleGate > 0 ? "/idle:" + DetentText.Num(IdleGate) : string.Empty);
            }
        }

        public static DetentTarget Parse(string text)
        {
            string s = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (s.Length == 0)
                return null;
            DetentTarget target = new DetentTarget();
            foreach (string part in s.Split('/'))
            {
                string p = part.Trim();
                double v;
                if (p == "full" || p == "noab")
                    target.Kind = DetentTargetKind.FullAtDetent;
                else if (p == "linear" || p == "none")
                    target.Kind = DetentTargetKind.Linear;
                else if (p.StartsWith("ab:", StringComparison.Ordinal) && DetentText.TryNum(p.Substring(3), out v) && v > 0 && v <= 1)
                {
                    target.Kind = DetentTargetKind.Afterburner;
                    target.AbStart = v;
                }
                else if (p.StartsWith("idle:", StringComparison.Ordinal) && DetentText.TryNum(p.Substring(5), out v) && v >= 0 && v < 1)
                    target.IdleGate = v;
                else
                    return null;
            }
            if (target.Kind == DetentTargetKind.Afterburner && target.IdleGate >= target.AbStart)
                return null;
            return target;
        }

        public bool SameAs(DetentTarget other)
        {
            return other != null && other.Kind == Kind &&
                   (Kind != DetentTargetKind.Afterburner || Math.Abs(other.AbStart - AbStart) < 0.0005) &&
                   Math.Abs(other.IdleGate - IdleGate) < 0.0005;
        }

        // "AB at 78 %", "no AB: full at the detent", "linear".
        public string Describe()
        {
            string idle = IdleGate > 0 ? ", idle at " + DetentText.Percent(IdleGate) : string.Empty;
            switch (Kind)
            {
                case DetentTargetKind.FullAtDetent: return "no afterburner: full power at the detent" + idle;
                case DetentTargetKind.Linear: return "no detent: the relation's curve";
                default: return "afterburner at " + DetentText.Percent(AbStart) + idle;
            }
        }
    }

    // A relation's detent mode, kept in Bind.TuningOverrides["detent"]:
    // "on=1;noab=full;unknown=linear;margin=0.01;t.DCS:F-16C_50=ab:0.78;t.IL2Game:IL2Game=linear".
    public sealed class DetentSettings
    {
        public const string Key = "detent";
        public const double DefaultMargin = 0.01;

        public bool On = true;
        // Aircraft known to have no afterburner.
        public DetentTargetKind NoAfterburner = DetentTargetKind.FullAtDetent;
        // Aircraft JoyPro has no gate data for.
        public DetentTargetKind Unknown = DetentTargetKind.Linear;
        // The device's detent gives the afterburner start minus this (game throttle units), so resting in the detent
        // stays just below the afterburner and pushing over it lights it.
        public double Margin = DefaultMargin;
        // "<game>:<plane>" -> DetentTarget text: the relation's own value for an aircraft (from the profile or the user).
        public Dictionary<string, string> Targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string TargetKey(string game, string plane)
        {
            return (game ?? string.Empty).Trim() + ":" + (plane ?? string.Empty).Trim();
        }

        public DetentTarget OwnTarget(string game, string plane)
        {
            string text;
            return Targets.TryGetValue(TargetKey(game, plane), out text) ? DetentTarget.Parse(text) : null;
        }

        public string Format()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("on=").Append(On ? "1" : "0");
            sb.Append(";noab=").Append(KindText(NoAfterburner));
            sb.Append(";unknown=").Append(KindText(Unknown));
            sb.Append(";margin=").Append(DetentText.Num(Margin));
            foreach (KeyValuePair<string, string> t in Targets.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append(";t.").Append(t.Key).Append('=').Append(t.Value);
            return sb.ToString();
        }

        public static DetentSettings Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            DetentSettings s = new DetentSettings();
            foreach (KeyValuePair<string, string> part in DetentText.Pairs(text))
            {
                double v;
                DetentTargetKind kind;
                if (part.Key == "on")
                    s.On = part.Value == "1" || string.Equals(part.Value, "true", StringComparison.OrdinalIgnoreCase);
                else if (part.Key == "noab" && TryKind(part.Value, out kind))
                    s.NoAfterburner = kind;
                else if (part.Key == "unknown" && TryKind(part.Value, out kind))
                    s.Unknown = kind;
                else if (part.Key == "margin" && DetentText.TryNum(part.Value, out v) && v >= 0 && v <= 0.1)
                    s.Margin = v;
                else if (part.Key.StartsWith("t.", StringComparison.Ordinal) && part.Key.Length > 2 && DetentTarget.Parse(part.Value) != null)
                    s.Targets[part.Key.Substring(2)] = DetentTarget.Parse(part.Value).Format();
            }
            return s;
        }

        public static string KindText(DetentTargetKind kind)
        {
            return kind == DetentTargetKind.FullAtDetent ? "full" : kind == DetentTargetKind.Linear ? "linear" : "ab";
        }

        public static bool TryKind(string text, out DetentTargetKind kind)
        {
            string s = (text ?? string.Empty).Trim().ToLowerInvariant();
            kind = DetentTargetKind.Linear;
            if (s == "full" || s == "detent")
            {
                kind = DetentTargetKind.FullAtDetent;
                return true;
            }
            return s == "linear";
        }
    }

    // How well a game's model reproduces the detent curve.
    public sealed class DetentFit
    {
        public AxisTuning Tuning;
        // Largest difference to the wanted curve, in game throttle (0..1).
        public double MaxError;
        // "dead zone + saturation", "user curve", "curve points", "game parameters"
        public string Method = string.Empty;
        // The game cannot express the curve well enough: it keeps its normal curve (Tuning is null).
        public bool Fallback;
        public string Warning = string.Empty;
    }

    public static class DetentMath
    {
        // Within this the fit counts as exact; up to Acceptable it is used with a note, above it with a warning, above
        // Unusable the game keeps its normal curve.
        public const double Exact = 0.002;
        public const double Acceptable = 0.02;
        public const double Unusable = 0.06;
        private const int Samples = 401;

        // The base's game throttle 0..1 along the raw travel u 0..1 (raw x = 2u - 1), turned so that it rises from idle
        // to full: sigma = +1 when the model's output rises from idle to full, -1 when it falls (the game's convention and
        // the relation's invert decide which; the calibration says which end is idle).
        public static Func<double, double> BaseThrottle(AxisModel model, AxisTuning baseTuning, DetentCalibration cal, out double sigma)
        {
            double yIdle = model.Evaluate(baseTuning, 2.0 * cal.Idle - 1.0);
            double yFull = model.Evaluate(baseTuning, 2.0 * cal.FullOrEnd - 1.0);
            sigma = yFull >= yIdle ? 1.0 : -1.0;
            if (Math.Abs(yFull - yIdle) < 1e-6)
                sigma = 0.0;
            double s = sigma;
            return u => (1.0 + s * model.Evaluate(baseTuning, 2.0 * u - 1.0)) / 2.0;
        }

        // The bend of the base's throttle g (0..1) into the aircraft's: piecewise linear through anchors (from -> to), with
        // (0, 0) and (1, 1) at the ends.
        public static List<KeyValuePair<double, double>> Anchors(Func<double, double> baseThrottle, DetentCalibration cal, DetentTarget target, double margin)
        {
            List<KeyValuePair<double, double>> anchors = new List<KeyValuePair<double, double>>();
            double gDet = baseThrottle(cal.Detent);
            double gFull = baseThrottle(cal.FullOrEnd);
            if (cal.HasCutoff)
            {
                double gCut = baseThrottle(cal.Cutoff);
                if (gCut > 1e-6)
                    anchors.Add(new KeyValuePair<double, double>(gCut, 0.0));
                anchors.Add(new KeyValuePair<double, double>(baseThrottle(cal.Idle), Math.Max(0.0, target.IdleGate)));
            }
            if (target.Kind == DetentTargetKind.FullAtDetent)
                anchors.Add(new KeyValuePair<double, double>(gDet, 1.0));
            else if (target.Kind == DetentTargetKind.Afterburner)
            {
                anchors.Add(new KeyValuePair<double, double>(gDet, Math.Max(0.0, Math.Min(1.0, target.AbStart - margin))));
                if (gFull < 1.0 - 1e-6)
                    anchors.Add(new KeyValuePair<double, double>(gFull, 1.0));
            }
            List<KeyValuePair<double, double>> sorted = new List<KeyValuePair<double, double>> { new KeyValuePair<double, double>(0.0, 0.0) };
            foreach (KeyValuePair<double, double> a in anchors.OrderBy(a => a.Key))
            {
                KeyValuePair<double, double> last = sorted[sorted.Count - 1];
                if (a.Key <= last.Key + 1e-6)
                {
                    // Same base value twice (e.g. a flat base): the later anchor wins, never falling.
                    if (sorted.Count > 1)
                        sorted[sorted.Count - 1] = new KeyValuePair<double, double>(last.Key, Math.Max(sorted[sorted.Count - 2].Value, a.Value));
                    continue;
                }
                sorted.Add(new KeyValuePair<double, double>(a.Key, Math.Max(last.Value, a.Value)));
            }
            KeyValuePair<double, double> end = sorted[sorted.Count - 1];
            if (end.Key < 1.0 - 1e-6)
                sorted.Add(new KeyValuePair<double, double>(1.0, Math.Max(end.Value, 1.0)));
            return sorted;
        }

        public static double Piecewise(List<KeyValuePair<double, double>> points, double v)
        {
            if (points == null || points.Count == 0)
                return v;
            if (v <= points[0].Key)
                return points[0].Value;
            for (int i = 1; i < points.Count; i++)
            {
                if (v <= points[i].Key)
                {
                    double span = points[i].Key - points[i - 1].Key;
                    return span <= 1e-12 ? points[i].Value : points[i - 1].Value + (points[i].Value - points[i - 1].Value) * (v - points[i - 1].Key) / span;
                }
            }
            return points[points.Count - 1].Value;
        }

        // The game throttle 0..1 an aircraft should get along the raw travel u 0..1.
        public static Func<double, double> WantedThrottle(AxisModel model, AxisTuning baseTuning, DetentCalibration cal, DetentTarget target, double margin)
        {
            double sigma;
            Func<double, double> g = BaseThrottle(model, baseTuning, cal, out sigma);
            if (sigma == 0.0 || target.Kind == DetentTargetKind.Linear)
                return g;
            List<KeyValuePair<double, double>> anchors = Anchors(g, cal, target, margin);
            return u => Piecewise(anchors, g(u));
        }

        // The model output (-1..1) an aircraft should get at raw x -1..1.
        public static Func<double, double> WantedResponse(AxisModel model, AxisTuning baseTuning, DetentCalibration cal, DetentTarget target, double margin)
        {
            double sigma;
            BaseThrottle(model, baseTuning, cal, out sigma);
            Func<double, double> wanted = WantedThrottle(model, baseTuning, cal, target, margin);
            double s = sigma == 0.0 ? 1.0 : sigma;
            return x => s * (2.0 * wanted((Math.Max(-1.0, Math.Min(1.0, x)) + 1.0) / 2.0) - 1.0);
        }

        // Largest difference between a tuning's response and the wanted response, in game throttle units (0..1).
        public static double MaxError(AxisModel model, AxisTuning tuning, Func<double, double> wantedResponse)
        {
            double max = 0.0;
            for (int i = 0; i < Samples; i++)
            {
                double x = -1.0 + 2.0 * i / (Samples - 1);
                max = Math.Max(max, Math.Abs(model.Evaluate(tuning, x) - wantedResponse(x)) / 2.0);
            }
            return max;
        }

        // The tuning of a game's model that gives the detent curve: DCS dead zone + saturation (exact for a straight
        // line) or its 11-point user curve; free curve points (X-Plane) exactly; any other model by searching its own
        // parameters on their grid. baseTuning: what the game uses without detent mode (in its own model).
        public static DetentFit Fit(AxisModel model, AxisTuning baseTuning, DetentCalibration cal, DetentTarget target, double margin)
        {
            Func<double, double> wanted = WantedResponse(model, baseTuning, cal, target, margin);
            DetentFit fit;
            if (model.Id == AxisModels.Dcs)
                fit = FitDcs(model, baseTuning, cal, wanted);
            else if (model.CurveStops != null)
                fit = FitControlPoints(model, baseTuning, cal, wanted);
            else
                fit = FitParameters(model, baseTuning, wanted);
            fit.MaxError = MaxError(model, fit.Tuning, wanted);
            if (fit.MaxError > Unusable)
            {
                fit.Fallback = true;
                fit.Warning = model.Title + " cannot express the detent curve (off by up to " + DetentText.Percent(fit.MaxError) +
                              " of the throttle): it keeps the normal curve.";
                fit.Tuning = null;
            }
            else if (fit.MaxError > Acceptable)
                fit.Warning = model.Title + " gets an approximation of the detent curve (off by up to " + DetentText.Percent(fit.MaxError) + " of the throttle).";
            return fit;
        }

        // DCS: Slider on; the shape s(u) the filter applies to the raw travel (output = +-(2 s - 1), invert flips it).
        private static DetentFit FitDcs(AxisModel model, AxisTuning baseTuning, DetentCalibration cal, Func<double, double> wanted)
        {
            AxisTuning t = baseTuning.Clone();
            t.Slider = true;
            t.ClearControlPoints();
            double[] us = new double[Samples];
            double[] shape = new double[Samples];
            // The invert that gives a rising shape (the same response either way).
            for (int pass = 0; pass < 2; pass++)
            {
                double iota = t.Inverted ? -1.0 : 1.0;
                for (int i = 0; i < Samples; i++)
                {
                    us[i] = i / (double)(Samples - 1);
                    shape[i] = (iota * wanted(2.0 * us[i] - 1.0) + 1.0) / 2.0;
                }
                if (shape[Samples - 1] >= shape[0])
                    break;
                t.Inverted = !t.Inverted;
            }

            // A straight line between a flat start at 0 and a flat end: dead zone, saturation X/Y (exact).
            int lastZero = -1;
            while (lastZero + 1 < Samples && shape[lastZero + 1] <= 1e-6)
                lastZero++;
            double sy = shape[Samples - 1];
            int firstTop = Samples - 1;
            while (firstTop - 1 >= 0 && shape[firstTop - 1] >= sy - 1e-6)
                firstTop--;
            if (lastZero >= 0 || shape[0] <= 1e-6)
            {
                double dz = lastZero < 0 ? 0.0 : RefineEdge(i => wanted(2.0 * i - 1.0), t.Inverted, us[lastZero], lastZero + 1 < Samples ? us[lastZero + 1] : 1.0, 0.0, true);
                double sx = firstTop <= 0 ? 1.0 : RefineEdge(i => wanted(2.0 * i - 1.0), t.Inverted, us[Math.Max(0, firstTop - 1)], us[firstTop], sy, false);
                if (sx > dz + 0.01 && sy > 0.01)
                {
                    bool straight = true;
                    for (int i = 0; i < Samples && straight; i++)
                    {
                        double line = us[i] <= dz ? 0.0 : us[i] >= sx ? sy : sy * (us[i] - dz) / (sx - dz);
                        straight = Math.Abs(line - shape[i]) <= 0.002;
                    }
                    if (straight)
                    {
                        AxisTuning line = t.Clone();
                        line.Deadzone = Math.Round(dz, 4);
                        line.SaturationX = Math.Round(sx, 4);
                        line.SaturationY = Math.Round(sy, 4);
                        line.Curvature = new List<double> { 0.0 };
                        return new DetentFit { Tuning = line, Method = "dead zone + saturation" };
                    }
                }
            }

            // The 11-point user curve (natural spline through 0, 0.1, .. 1), least squares over the travel, exact at the
            // device's detent.
            AxisTuning curve = t.Clone();
            curve.Deadzone = 0.0;
            curve.SaturationX = 1.0;
            curve.SaturationY = 1.0;
            Func<double, double> shapeAt = u => (((curve.Inverted ? -1.0 : 1.0) * wanted(2.0 * u - 1.0)) + 1.0) / 2.0;
            // Knots on a flat stretch (full beyond the detent, nothing in a cut-off) stay exactly flat.
            List<double> exact = new List<double> { cal.Detent };
            for (int k = 0; k < AxisModels.UserCurvePoints; k++)
            {
                double u = k / (double)(AxisModels.UserCurvePoints - 1);
                double here = shapeAt(u);
                if (Math.Abs(shapeAt(Math.Max(0.0, u - 0.05)) - here) < 1e-6 && Math.Abs(shapeAt(Math.Min(1.0, u + 0.05)) - here) < 1e-6)
                    exact.Add(u);
            }
            curve.Curvature = FitUserCurve(shapeAt, exact);
            return new DetentFit { Tuning = curve, Method = "user curve" };
        }

        // Where the wanted shape leaves 0 (start) or reaches its top (end) between two samples (bisection).
        private static double RefineEdge(Func<double, double> wantedAtU, bool inverted, double lo, double hi, double level, bool leaving)
        {
            double iota = inverted ? -1.0 : 1.0;
            Func<double, double> shape = u => (iota * wantedAtU(u) + 1.0) / 2.0;
            for (int i = 0; i < 40; i++)
            {
                double mid = (lo + hi) / 2.0;
                bool past = leaving ? shape(mid) > level + 1e-7 : shape(mid) >= level - 1e-7;
                if (past)
                    hi = mid;
                else
                    lo = mid;
            }
            return leaving ? lo : hi;
        }

        // The 11 outputs whose natural spline comes closest to shape(u) (least squares on 201 points), passing exactly
        // through shape at the given positions; clamped to 0..1.
        public static List<double> FitUserCurve(Func<double, double> shape, IEnumerable<double> exactAt)
        {
            int n = AxisModels.UserCurvePoints;
            const int m = 201;
            double[,] basis = new double[m, n];
            double[] target = new double[m];
            for (int k = 0; k < m; k++)
            {
                double u = k / (double)(m - 1);
                target[k] = shape(u);
                for (int j = 0; j < n; j++)
                    basis[k, j] = SplineBasis(j, u);
            }
            List<double[]> constraints = new List<double[]>();
            List<double> values = new List<double>();
            foreach (double at in (exactAt ?? Enumerable.Empty<double>()).Concat(new[] { 0.0, 1.0 }))
            {
                double u = Math.Max(0.0, Math.Min(1.0, at));
                if (constraints.Any(c => Math.Abs(c[n] - u) < 1e-6))
                    continue;
                double[] row = new double[n + 1];
                for (int j = 0; j < n; j++)
                    row[j] = SplineBasis(j, u);
                row[n] = u;
                constraints.Add(row);
                values.Add(shape(u));
            }
            int size = n + constraints.Count;
            double[,] a = new double[size, size + 1];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < m; k++)
                        sum += basis[k, i] * basis[k, j];
                    a[i, j] = 2.0 * sum;
                }
                double r = 0;
                for (int k = 0; k < m; k++)
                    r += basis[k, i] * target[k];
                a[i, size] = 2.0 * r;
            }
            for (int c = 0; c < constraints.Count; c++)
            {
                for (int j = 0; j < n; j++)
                {
                    a[n + c, j] = constraints[c][j];
                    a[j, n + c] = constraints[c][j];
                }
                a[n + c, size] = values[c];
            }
            double[] solution = Solve(a, size);
            List<double> curve = new List<double>();
            for (int j = 0; j < n; j++)
            {
                double v = solution == null ? shape(j / (double)(n - 1)) : solution[j];
                curve.Add(Math.Round(Math.Max(0.0, Math.Min(1.0, v)), 4));
            }
            return curve;
        }

        private static double SplineBasis(int index, double u)
        {
            List<double> unit = new List<double>();
            for (int j = 0; j < AxisModels.UserCurvePoints; j++)
                unit.Add(j == index ? 1.0 : 0.0);
            return AxisModels.NaturalSpline(unit, u);
        }

        // Gaussian elimination with partial pivoting; a: size x (size + 1). null when singular.
        private static double[] Solve(double[,] a, int size)
        {
            for (int col = 0; col < size; col++)
            {
                int pivot = col;
                for (int r = col + 1; r < size; r++)
                {
                    if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                        pivot = r;
                }
                if (Math.Abs(a[pivot, col]) < 1e-12)
                    return null;
                if (pivot != col)
                {
                    for (int c = 0; c <= size; c++)
                    {
                        double tmp = a[col, c];
                        a[col, c] = a[pivot, c];
                        a[pivot, c] = tmp;
                    }
                }
                for (int r = 0; r < size; r++)
                {
                    if (r == col)
                        continue;
                    double f = a[r, col] / a[col, col];
                    if (f == 0)
                        continue;
                    for (int c = col; c <= size; c++)
                        a[r, c] -= f * a[col, c];
                }
            }
            double[] x = new double[size];
            for (int i = 0; i < size; i++)
                x[i] = a[i, size] / a[i, i];
            return x;
        }

        // Free curve points (X-Plane): LINEAR stops on the travel the curve gets (after the model's invert), dense where
        // the wanted curve bends, with stops at the calibrated positions.
        private static DetentFit FitControlPoints(AxisModel model, AxisTuning baseTuning, DetentCalibration cal, Func<double, double> wanted)
        {
            AxisTuning t = baseTuning.Clone();
            t.Slider = true;
            t.Deadzone = 0.0;
            t.SaturationX = 1.0;
            t.SaturationY = 1.0;
            t.Curvature = new List<double> { 0.0 };
            t.Native["interp"] = 0;
            // Which way the model feeds the travel into its curve: probe with an identity curve.
            AxisTuning probe = t.Clone();
            probe.SetControlPoints(new List<KeyValuePair<double, double>> { new KeyValuePair<double, double>(0, 0), new KeyValuePair<double, double>(1, 1) });
            bool flipsInput = model.Evaluate(probe, 0.5) < 0;
            Func<double, double> stop = v => Math.Max(0.0, Math.Min(1.0, (wanted(flipsInput ? 1.0 - 2.0 * v : 2.0 * v - 1.0) + 1.0) / 2.0));
            List<double> breaks = new List<double> { cal.Idle, cal.Detent, cal.FullOrEnd };
            if (cal.HasCutoff)
                breaks.Add(cal.Cutoff);
            List<KeyValuePair<double, double>> points = SampleLinear(stop, breaks.Select(b => flipsInput ? 1.0 - b : b));
            // The wanted curve also bends where the base's own stops are; refine around them through SampleLinear's
            // subdivision. Then drop stops on a straight line.
            t.SetControlPoints(Simplify(points));
            return new DetentFit { Tuning = t, Method = "curve points" };
        }

        public static List<KeyValuePair<double, double>> SampleLinear(Func<double, double> f, IEnumerable<double> breaks)
        {
            List<double> xs = new List<double> { 0.0, 1.0 };
            xs.AddRange(breaks.Where(b => b > 0 && b < 1));
            xs = xs.Select(v => Math.Round(v, 6)).Distinct().OrderBy(v => v).ToList();
            List<KeyValuePair<double, double>> points = new List<KeyValuePair<double, double>>();
            for (int i = 0; i + 1 < xs.Count; i++)
            {
                double a = xs[i];
                double b = xs[i + 1];
                double fa = f(a);
                points.Add(new KeyValuePair<double, double>(a, fa));
                Subdivide(f, a, fa, b, f(b), 0, points);
            }
            points.Add(new KeyValuePair<double, double>(1.0, f(1.0)));
            return points.Select(p => new KeyValuePair<double, double>(Math.Round(p.Key, 6), Math.Round(p.Value, 6))).ToList();
        }

        private static void Subdivide(Func<double, double> f, double a, double fa, double b, double fb, int depth, List<KeyValuePair<double, double>> points)
        {
            bool close = true;
            foreach (double t in new[] { 0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875 })
            {
                double x = a + (b - a) * t;
                if (Math.Abs(f(x) - (fa + (fb - fa) * t)) > 0.0005)
                    close = false;
            }
            if (close || depth >= 9 || b - a < 1.0 / 1024)
                return;
            double m = (a + b) / 2.0;
            double fm = f(m);
            Subdivide(f, a, fa, m, fm, depth + 1, points);
            points.Add(new KeyValuePair<double, double>(m, fm));
            Subdivide(f, m, fm, b, fb, depth + 1, points);
        }

        private static List<KeyValuePair<double, double>> Simplify(List<KeyValuePair<double, double>> points)
        {
            List<KeyValuePair<double, double>> result = new List<KeyValuePair<double, double>>();
            foreach (KeyValuePair<double, double> p in points.OrderBy(p => p.Key))
            {
                if (result.Count > 0 && Math.Abs(result[result.Count - 1].Key - p.Key) < 1e-6)
                {
                    result[result.Count - 1] = p;
                    continue;
                }
                if (result.Count >= 2)
                {
                    KeyValuePair<double, double> a = result[result.Count - 2];
                    KeyValuePair<double, double> b = result[result.Count - 1];
                    double t = (b.Key - a.Key) / (p.Key - a.Key);
                    if (Math.Abs(a.Value + (p.Value - a.Value) * t - b.Value) < 1e-6)
                        result[result.Count - 1] = p;
                    else
                        result.Add(p);
                }
                else
                    result.Add(p);
            }
            return result;
        }

        // Any other model: its own number parameters (and dead zone / saturation where it has them), searched on their
        // grid coordinate by coordinate from the game's normal tuning; the one-sided switch on.
        private static DetentFit FitParameters(AxisModel model, AxisTuning baseTuning, Func<double, double> wanted)
        {
            AxisTuning best = baseTuning.Clone();
            if (model.Parameters.Any(p => p.Key == "slider"))
                best.Slider = true;
            double bestError = MaxError(model, best, wanted);
            List<AxisParameter> parameters = model.Parameters.Where(p => p.Kind == AxisParameterKind.Number && p.Max > p.Min).ToList();
            for (int pass = 0; pass < 3 && bestError > Exact; pass++)
            {
                bool improved = false;
                foreach (AxisParameter parameter in parameters)
                {
                    double step = parameter.Step > 0 ? parameter.Step : (parameter.Max - parameter.Min) / 100.0;
                    int count = (int)Math.Round((parameter.Max - parameter.Min) / step);
                    if (count > 200)
                    {
                        step = (parameter.Max - parameter.Min) / 200.0;
                        count = 200;
                    }
                    for (int i = 0; i <= count; i++)
                    {
                        AxisTuning candidate = best.Clone();
                        WriteParameter(candidate, parameter.Key, parameter.Min + step * i);
                        double error = MaxError(model, candidate, wanted);
                        if (error < bestError - 1e-9)
                        {
                            bestError = error;
                            best = candidate;
                            improved = true;
                        }
                    }
                }
                if (!improved)
                    break;
            }
            return new DetentFit { Tuning = best, Method = "game parameters" };
        }

        private static void WriteParameter(AxisTuning t, string key, double value)
        {
            value = Math.Round(value, 6);
            switch (key)
            {
                case "deadzone": t.Deadzone = value; break;
                case "saturationX": t.SaturationX = value; break;
                case "saturationY": t.SaturationY = value; break;
                case "curvature": t.Curvature = new List<double> { value }; break;
                default: t.Native[key] = value; break;
            }
        }

        // Where the device's detent is, read from a curve that gives full output there and stays full beyond it (a
        // relation for aircraft without afterburner): the first travel position where the game throttle reaches 99.9 % and
        // stays (above 99.5 %) to the end. NaN when the curve only gets full in the last 8 % of the travel (no plateau: a normal
        // curve, not one that ends at a detent).
        public static double DetentFromFullCurve(Func<double, double> throttleAtTravel)
        {
            int first = -1;
            for (int i = 0; i <= 1000; i++)
            {
                double v = throttleAtTravel(i / 1000.0);
                if (v >= 0.999)
                {
                    if (first < 0)
                        first = i;
                }
                else if (v < 0.995)
                    first = -1;
            }
            if (first < 0 || first > 920)
                return double.NaN;
            return first / 1000.0;
        }

        // The raw position where a curve gives a game throttle (first crossing), NaN when it never does.
        public static double PositionOf(Func<double, double> throttleAtU, double throttle)
        {
            double previous = throttleAtU(0.0);
            for (int i = 1; i <= 1000; i++)
            {
                double u = i / 1000.0;
                double v = throttleAtU(u);
                if ((previous - throttle) * (v - throttle) <= 0 && Math.Abs(v - previous) > 1e-12)
                    return (i - 1) / 1000.0 + (throttle - previous) / (v - previous) / 1000.0;
                previous = v;
            }
            return double.NaN;
        }
    }

    internal static class DetentText
    {
        public static IEnumerable<KeyValuePair<string, string>> Pairs(string text)
        {
            foreach (string part in (text ?? string.Empty).Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0)
                    continue;
                yield return new KeyValuePair<string, string>(part.Substring(0, eq).Trim(), part.Substring(eq + 1).Trim());
            }
        }

        public static bool TryNum(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public static string Num(double value)
        {
            return Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
        }

        public static string Percent(double fraction)
        {
            double p = fraction * 100.0;
            return (Math.Abs(p - Math.Round(p)) < 0.05 ? Math.Round(p).ToString("0", CultureInfo.InvariantCulture) : p.ToString("0.0", CultureInfo.InvariantCulture)) + " %";
        }
    }
}
