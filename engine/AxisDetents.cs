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

        // The raw position of a share of the travel (Travel's inverse).
        public double RawAt(double travel)
        {
            double t = Math.Max(0.0, Math.Min(1.0, travel));
            return Increasing ? t : 1.0 - t;
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
    // "on=1;noab=full;unknown=linear;margin=0.02;band=0.02;t.DCS:F-16C_50=ab:0.78;t.IL2Game:IL2Game=linear".
    public sealed class DetentSettings
    {
        public const string Key = "detent";
        public const double DefaultMargin = 0.02;
        // Up to 2.7.3 every setting was written with margin=0.01 (the default then; the GUI never offered another). Texts
        // without a band (written before 2.7.4) read that as the new default.
        public const double LegacyMargin = 0.01;
        public const double DefaultBand = 0.02;
        public const double MaxBand = 0.05;

        public bool On = true;
        // Aircraft known to have no afterburner.
        public DetentTargetKind NoAfterburner = DetentTargetKind.FullAtDetent;
        // Aircraft JoyPro has no gate data for.
        public DetentTargetKind Unknown = DetentTargetKind.Linear;
        // The device's detent gives the afterburner start minus this (game throttle units), so resting in the detent
        // stays below the afterburner and pushing over it lights it.
        public double Margin = DefaultMargin;
        // The detent band (share of the travel): up to this far past the calibrated detent the throttle stays below the
        // afterburner start (at most the start minus a quarter of the margin), whatever the curve or the game's curve
        // resolution, so a lever resting a little into its detent, sensor noise or two levers calibrated a little
        // differently never light the afterburner on one engine only (2.7.4).
        public double Band = DefaultBand;
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
            sb.Append(";band=").Append(DetentText.Num(Band));
            foreach (KeyValuePair<string, string> t in Targets.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append(";t.").Append(t.Key).Append('=').Append(t.Value);
            return sb.ToString();
        }

        public static DetentSettings Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            DetentSettings s = new DetentSettings();
            bool hasBand = false;
            foreach (KeyValuePair<string, string> part in DetentText.Pairs(text))
            {
                double v;
                DetentTargetKind kind;
                if (part.Key == "band" && DetentText.TryNum(part.Value, out v) && v >= 0 && v <= MaxBand)
                {
                    s.Band = v;
                    hasBand = true;
                }
                else if (part.Key == "on")
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
            if (!hasBand && Math.Abs(s.Margin - LegacyMargin) < 1e-9)
                s.Margin = DefaultMargin;
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

        // The base's game throttle made non-decreasing along the travel from the back end to the front end: a throttle
        // never falls while the lever goes forward, even where the relation's own curve dips (2.7.4 report: a MiG-21 user
        // curve with 92 % at 70 % and 91 % at 80 % of the travel gave a bent curve whose 80 % point was below its 70 %
        // point). A base that never falls is returned as it is.
        public static Func<double, double> MonotoneThrottle(Func<double, double> g, DetentCalibration cal)
        {
            const int n = 2000;
            double[] prefix = new double[n + 1];
            double max = double.NegativeInfinity;
            bool falls = false;
            for (int i = 0; i <= n; i++)
            {
                double v = g(cal.RawAt(i / (double)n));
                if (v < max - 1e-9)
                    falls = true;
                max = Math.Max(max, v);
                prefix[i] = max;
            }
            if (!falls)
                return g;
            return u =>
            {
                int k = Math.Max(0, Math.Min(n, (int)Math.Floor(cal.Travel(u) * n + 1e-9)));
                return Math.Max(g(u), prefix[k]);
            };
        }

        // The detent band actually used (share of the travel past the detent): the setting, at most a quarter of the
        // travel between idle and the detent and between the detent and full. 0 for aircraft without afterburner (beyond
        // their detent it is full power anyway).
        public static double EffectiveBand(DetentCalibration cal, DetentTarget target, double band)
        {
            if (cal == null || target == null || target.Kind != DetentTargetKind.Afterburner || !(band > 0))
                return 0.0;
            double idle = cal.Travel(cal.Idle);
            double detent = cal.Travel(cal.Detent);
            double full = cal.Travel(cal.FullOrEnd);
            return Math.Max(0.0, Math.Min(band, Math.Min((detent - idle) / 4.0, (full - detent) / 4.0)));
        }

        // The most the throttle may give up to the front edge of the detent band: the afterburner start minus a quarter of
        // the margin (with the default margin 2 %: 0.5 % below the afterburner).
        public static double BandCeiling(DetentTarget target, double margin)
        {
            return Math.Max(0.0, Math.Min(1.0, target.AbStart - Math.Max(0.0, margin) / 4.0));
        }

        // The bend along the travel (share 0..1 from the back end): anchors (travel -> game throttle), rising. Afterburner:
        // the detent gives the afterburner start minus the margin, full gives 1; where the way from the detent to full
        // would pass the band's ceiling inside the detent band (a steep afterburner range), the detent gets less, just so
        // much that the band's front edge gives the ceiling (no extra bend: an 11-point DCS curve could not follow a
        // corner that close to the detent). No afterburner: full at the detent. With a cut-off the bend starts at the idle detent with the
        // aircraft's idle (behind it WantedThrottle's cut-off ramp), else at the back end with 0. g: the base's throttle
        // along the raw travel (for the band check; null: straight in travel).
        public static List<KeyValuePair<double, double>> Anchors(DetentCalibration cal, DetentTarget target, double margin, double band,
            Func<double, double> g = null)
        {
            List<KeyValuePair<double, double>> anchors = new List<KeyValuePair<double, double>>();
            double detent = cal.Travel(cal.Detent);
            double full = cal.Travel(cal.FullOrEnd);
            anchors.Add(cal.HasCutoff
                ? new KeyValuePair<double, double>(cal.Travel(cal.Idle), Math.Max(0.0, target.IdleGate))
                : new KeyValuePair<double, double>(0.0, 0.0));
            if (target.Kind == DetentTargetKind.FullAtDetent)
                anchors.Add(new KeyValuePair<double, double>(detent, 1.0));
            else if (target.Kind == DetentTargetKind.Afterburner)
            {
                anchors.Add(new KeyValuePair<double, double>(detent, RestAtDetent(cal, target, margin, band, g)));
                anchors.Add(new KeyValuePair<double, double>(full, 1.0));
            }
            List<KeyValuePair<double, double>> sorted = new List<KeyValuePair<double, double>> { anchors[0] };
            foreach (KeyValuePair<double, double> a in anchors.Skip(1))
            {
                KeyValuePair<double, double> last = sorted[sorted.Count - 1];
                if (a.Key <= last.Key + 1e-9)
                {
                    // The same travel twice (a calibration at the end of the travel): the later anchor wins, never falling.
                    if (sorted.Count > 1)
                        sorted[sorted.Count - 1] = new KeyValuePair<double, double>(last.Key, Math.Max(sorted[sorted.Count - 2].Value, a.Value));
                    continue;
                }
                sorted.Add(new KeyValuePair<double, double>(a.Key, Math.Max(last.Value, a.Value)));
            }
            KeyValuePair<double, double> end = sorted[sorted.Count - 1];
            if (end.Key < 1.0 - 1e-9)
                sorted.Add(new KeyValuePair<double, double>(1.0, Math.Max(end.Value, 1.0)));
            return sorted;
        }

        // The game throttle 0..1 an aircraft should get along the raw travel u 0..1: between two anchors the base's own
        // throttle g scaled into the anchors' range (the relation's curve shape is kept inside each segment; where g is
        // flat there, straight in travel). The first segment without a cut-off scales g from 0, as before 2.7.4.
        // The base's dead zone is part of g (the game applies it to the raw axis at the idle end of the travel, before
        // the bend): the throttle stays at zero there and the device's detent still gives the afterburner start. With
        // a cut-off the calibrated idle detent always gives the aircraft's idle: behind it the throttle runs straight
        // from 0 at the cut-off to idle (a dead zone there changes nothing), and a dead zone reaching past the idle
        // detent keeps the throttle at idle up to its end (never at the cut-off while resting in the idle detent).
        // The game throttle resting in the detent gives: the afterburner start minus the margin, or less where the way to
        // full is so steep that the front edge of the detent band would pass the band's ceiling (rest + (1 - rest) f =
        // ceiling, f: the share of the way from the detent to full the base covers inside the band).
        public static double RestAtDetent(DetentCalibration cal, DetentTarget target, double margin, double band, Func<double, double> g = null)
        {
            double rest = Math.Max(0.0, Math.Min(1.0, target.AbStart - margin));
            double b = EffectiveBand(cal, target, band);
            double detent = cal.Travel(cal.Detent);
            double full = cal.Travel(cal.FullOrEnd);
            if (b <= 0 || full <= detent + 1e-6)
                return rest;
            double f = Math.Min(1.0, b / (full - detent));
            if (g != null)
            {
                double gDetent = g(cal.RawAt(detent));
                double gFull = g(cal.RawAt(full));
                if (gFull - gDetent > 1e-9)
                    f = Math.Max(0.0, Math.Min(1.0, (g(cal.RawAt(detent + b)) - gDetent) / (gFull - gDetent)));
            }
            // A hair below the ceiling: games that sample the curve (X-Plane's stops) stay below it too.
            double ceiling = BandCeiling(target, margin) - 0.001;
            if (f >= 1.0 - 1e-9 || rest + (1.0 - rest) * f <= ceiling)
                return rest;
            return Math.Max(0.0, Math.Min(rest, (ceiling - f) / (1.0 - f)));
        }

        public static Func<double, double> WantedThrottle(AxisModel model, AxisTuning baseTuning, DetentCalibration cal, DetentTarget target, double margin,
            double band = DetentSettings.DefaultBand)
        {
            double sigma;
            Func<double, double> raw = BaseThrottle(model, baseTuning, cal, out sigma);
            if (sigma == 0.0 || target.Kind == DetentTargetKind.Linear)
                return raw;
            Func<double, double> g = MonotoneThrottle(raw, cal);
            List<KeyValuePair<double, double>> anchors = Anchors(cal, target, margin, band, g);
            double[] at = new double[anchors.Count];
            for (int i = 0; i < anchors.Count; i++)
                at[i] = i == 0 && !cal.HasCutoff ? 0.0 : g(cal.RawAt(anchors[i].Key));
            Func<double, double> bent = u => Bend(anchors, at, cal.Travel(u), g(u));
            if (!cal.HasCutoff)
                return bent;
            double cutTravel = cal.Travel(cal.Cutoff);
            double idleTravel = cal.Travel(cal.Idle);
            double idleValue = anchors[0].Value;
            return u =>
            {
                double travel = cal.Travel(u);
                if (travel >= idleTravel)
                    return bent(u);
                if (travel <= cutTravel || idleTravel - cutTravel <= 1e-9)
                    return 0.0;
                return idleValue * (travel - cutTravel) / (idleTravel - cutTravel);
            };
        }

        private static double Bend(List<KeyValuePair<double, double>> anchors, double[] at, double travel, double g)
        {
            int i = 1;
            while (i < anchors.Count - 1 && travel > anchors[i].Key)
                i++;
            KeyValuePair<double, double> a = anchors[i - 1];
            KeyValuePair<double, double> b = anchors[i];
            double frac;
            if (at[i] - at[i - 1] > 1e-9)
                frac = (g - at[i - 1]) / (at[i] - at[i - 1]);
            else
                frac = b.Key - a.Key > 1e-12 ? (travel - a.Key) / (b.Key - a.Key) : 1.0;
            return a.Value + (b.Value - a.Value) * Math.Max(0.0, Math.Min(1.0, frac));
        }

        // The model output (-1..1) an aircraft should get at raw x -1..1.
        public static Func<double, double> WantedResponse(AxisModel model, AxisTuning baseTuning, DetentCalibration cal, DetentTarget target, double margin,
            double band = DetentSettings.DefaultBand)
        {
            double sigma;
            BaseThrottle(model, baseTuning, cal, out sigma);
            Func<double, double> wanted = WantedThrottle(model, baseTuning, cal, target, margin, band);
            double s = sigma == 0.0 ? 1.0 : sigma;
            return x => s * (2.0 * wanted((Math.Max(-1.0, Math.Min(1.0, x)) + 1.0) / 2.0) - 1.0);
        }

        // Largest difference between a tuning's response and the wanted response, in game throttle units (0..1). With a
        // calibrated cut-off the travel between the cut-off and the idle detent only has to stay between zero and idle
        // (the lever only passes through there; DCS gets a step at the idle detent), the cut-off itself must be zero.
        public static double MaxError(AxisModel model, AxisTuning tuning, Func<double, double> wantedResponse, DetentCalibration cal = null)
        {
            bool cutoff = cal != null && cal.HasCutoff;
            double cutTravel = cutoff ? cal.Travel(cal.Cutoff) : 0.0;
            double idleTravel = cutoff ? cal.Travel(cal.Idle) : 0.0;
            double atCut = cutoff ? wantedResponse(2.0 * cal.Cutoff - 1.0) : 0.0;
            double atIdle = cutoff ? wantedResponse(2.0 * cal.Idle - 1.0) : 0.0;
            double max = 0.0;
            for (int i = 0; i < Samples; i++)
            {
                double x = -1.0 + 2.0 * i / (Samples - 1);
                double y = model.Evaluate(tuning, x);
                double travel = cutoff ? cal.Travel((x + 1.0) / 2.0) : 1.0;
                double error;
                if (cutoff && travel > cutTravel + 1e-9 && travel < idleTravel - 1e-9)
                {
                    double lo = Math.Min(atCut, atIdle);
                    double hi = Math.Max(atCut, atIdle);
                    error = y < lo ? lo - y : y > hi ? y - hi : 0.0;
                }
                else
                    error = Math.Abs(y - wantedResponse(x));
                max = Math.Max(max, error / 2.0);
            }
            return max;
        }

        // The tuning of a game's model that gives the detent curve: DCS dead zone + saturation (exact for a straight
        // line) or its 11-point user curve; free curve points (X-Plane) exactly; any other model by searching its own
        // parameters on their grid. baseTuning: what the game uses without detent mode (in its own model).
        public static DetentFit Fit(AxisModel model, AxisTuning baseTuning, DetentCalibration cal, DetentTarget target, double margin,
            double band = DetentSettings.DefaultBand)
        {
            Func<double, double> wanted = WantedResponse(model, baseTuning, cal, target, margin, band);
            DetentFit fit;
            if (model.Id == AxisModels.Dcs)
                fit = FitDcs(model, baseTuning, cal, wanted, RestLimits(model, baseTuning, cal, target, margin, band));
            else if (model.CurveStops != null)
                fit = FitControlPoints(model, baseTuning, cal, wanted);
            else
                fit = FitParameters(model, baseTuning, wanted);
            fit.MaxError = MaxError(model, fit.Tuning, wanted, cal);
            double drop = LargestDrop(model, fit.Tuning, baseTuning, cal);
            if (drop > MaxDrop)
            {
                fit.Fallback = true;
                fit.Warning = model.Title + " cannot express the detent curve without the throttle falling somewhere (by " + DetentText.Percent(drop) +
                              "): it keeps the normal curve.";
                fit.Tuning = null;
            }
            else if (fit.MaxError > Unusable && model.Id != AxisModels.Dcs)
            {
                // DCS's fit is always exact at the detent, below the afterburner in the band and rising: closer than its
                // normal curve however far it is off elsewhere (2.7.4; it fell back to the normal curve before).
                fit.Fallback = true;
                fit.Warning = model.Title + " cannot express the detent curve (off by up to " + DetentText.Percent(fit.MaxError) +
                              " of the throttle): it keeps the normal curve.";
                fit.Tuning = null;
            }
            else if (fit.MaxError > Acceptable)
                fit.Warning = model.Title + " gets an approximation of the detent curve (off by up to " + DetentText.Percent(fit.MaxError) + " of the throttle).";
            return fit;
        }

        // A fitted curve may not let the throttle fall while the lever goes forward by more than this (game throttle).
        public const double MaxDrop = 0.0005;

        // The largest fall of the game throttle along the travel from the back end to the front end (0 = never falls).
        // The direction (sigma) comes from the base, as the bend was made.
        public static double LargestDrop(AxisModel model, AxisTuning tuning, AxisTuning baseTuning, DetentCalibration cal)
        {
            if (tuning == null)
                return 0.0;
            double sigma;
            BaseThrottle(model, baseTuning, cal, out sigma);
            double s = sigma == 0.0 ? 1.0 : sigma;
            double max = double.NegativeInfinity;
            double drop = 0.0;
            for (int i = 0; i <= 2000; i++)
            {
                double v = (1.0 + s * model.Evaluate(tuning, 2.0 * cal.RawAt(i / 2000.0) - 1.0)) / 2.0;
                max = Math.Max(max, v);
                drop = Math.Max(drop, max - v);
            }
            return drop;
        }

        // The detent band's ceiling for a fit (an aircraft with an afterburner and a band): the throttle stays at most
        // Ceiling up to the band's front edge (FrontRaw, a raw position); null otherwise.
        private sealed class RestLimit
        {
            public double FrontRaw;
            public double Ceiling;
            // The base's direction (BaseThrottle's sigma).
            public double Sigma;
        }

        private static RestLimit RestLimits(AxisModel model, AxisTuning baseTuning, DetentCalibration cal, DetentTarget target, double margin, double band)
        {
            double b = EffectiveBand(cal, target, band);
            if (b <= 0)
                return null;
            double sigma;
            BaseThrottle(model, baseTuning, cal, out sigma);
            if (sigma == 0.0)
                return null;
            double detent = cal.Travel(cal.Detent);
            return new RestLimit { FrontRaw = cal.RawAt(detent + b), Ceiling = BandCeiling(target, margin), Sigma = sigma };
        }

        // DCS: Slider on; the shape s(u) the filter applies to the raw travel (output = +-(2 s - 1), invert flips it).
        private static DetentFit FitDcs(AxisModel model, AxisTuning baseTuning, DetentCalibration cal, Func<double, double> wanted, RestLimit rest)
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
                    if (straight && rest != null)
                    {
                        // The line may not pass the detent band's ceiling (it is only within 0.2 % of the wanted curve).
                        double u = rest.FrontRaw;
                        double line = u <= dz ? 0.0 : u >= sx ? sy : sy * (u - dz) / (sx - dz);
                        double throttle = rest.Sigma * (t.Inverted ? -1.0 : 1.0) > 0 ? line : 1.0 - line;
                        straight = throttle <= rest.Ceiling - 0.0005;
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

            // The 11-point user curve (natural spline through 0, 0.1, .. 1), least squares over the travel. DCS applies dead
            // zone and saturation X before the user curve, so the flat ends of the wanted shape (the base's dead zone at the
            // start of the travel, a cut-off, full beyond the detent) go there and stay exactly flat; the spline only spans
            // the travel between them. It never falls (2.7.4: a spline through a bend could dip) and, with a detent band,
            // passes exactly through the detent and stays below the afterburner over the whole detent band.
            AxisTuning curve = t.Clone();
            double flatStart = 0.0;
            double flatEnd = 1.0;
            double top = 1.0;
            if (lastZero >= 0 && lastZero + 1 < Samples)
                flatStart = Math.Round(RefineEdge(i => wanted(2.0 * i - 1.0), t.Inverted, us[lastZero], us[lastZero + 1], 0.0, true), 4);
            if (firstTop > 0 && firstTop < Samples - 1)
            {
                flatEnd = Math.Round(RefineEdge(i => wanted(2.0 * i - 1.0), t.Inverted, us[firstTop - 1], us[firstTop], sy, false), 4);
                top = Math.Round(sy, 4);
            }
            // A cut-off: the spline starts just behind the idle detent with the aircraft's idle (a step there, as the real
            // lever goes over its idle detent into the cut-off); its kink at idle would bend the whole curve otherwise.
            double clampLow = 0.0;
            double clampHigh = 1.0;
            if (cal.HasCutoff)
            {
                double step = Math.Min(0.005, Math.Abs(cal.Idle - cal.Cutoff) / 3.0);
                if (cal.Increasing)
                {
                    flatStart = Math.Max(flatStart, Math.Round(cal.Idle - step, 4));
                    clampLow = cal.Idle;
                }
                else
                {
                    flatEnd = Math.Min(flatEnd, Math.Round(cal.Idle + step, 4));
                    top = Math.Round(sy, 4);
                    clampHigh = cal.Idle;
                }
            }
            if (flatEnd - flatStart < 0.1)
            {
                flatStart = 0.0;
                flatEnd = 1.0;
                top = 1.0;
                clampLow = 0.0;
                clampHigh = 1.0;
            }
            curve.Deadzone = flatStart;
            curve.SaturationX = flatEnd;
            curve.SaturationY = top;
            Func<double, double> shapeOnTravel = u => (((curve.Inverted ? -1.0 : 1.0) * wanted(2.0 * u - 1.0)) + 1.0) / 2.0;
            Func<double, double> shapeAt = v => shapeOnTravel(Math.Max(clampLow, Math.Min(clampHigh, flatStart + Math.Max(0.0, Math.Min(1.0, v)) * (flatEnd - flatStart))));
            Func<double, double> vOf = raw => (raw - flatStart) / (flatEnd - flatStart);
            // With a cut-off the spline's end at the idle side is the aircraft's idle (exact), so resting in the idle detent
            // never gives the cut-off. Knots on a flat stretch (full beyond the detent, nothing in a cut-off) stay exactly
            // flat.
            List<double> exact = new List<double>();
            List<ShapeBound> bounds = new List<ShapeBound>();
            double detentAt = vOf(cal.Detent);
            if (detentAt > 0.0 && detentAt < 1.0)
                exact.Add(detentAt);
            if (rest != null)
            {
                // The game throttle is the shape (kappa +1) or 1 - the shape (-1). Up to the band's front edge (the spline
                // rises, so its front edge is enough) at most the ceiling, a hair below it so rounding the points to 4
                // decimals cannot cross it.
                double kappa = rest.Sigma * (curve.Inverted ? -1.0 : 1.0);
                double front = vOf(rest.FrontRaw);
                double ceiling = rest.Ceiling - 0.0005;
                if (front > 0.0 && front < 1.0)
                    bounds.Add(kappa > 0 ? new ShapeBound(front, 1.0, ceiling) : new ShapeBound(front, -1.0, -(1.0 - ceiling)));
            }
            for (int k = 0; k < AxisModels.UserCurvePoints; k++)
            {
                double u = k / (double)(AxisModels.UserCurvePoints - 1);
                double here = shapeAt(u);
                if (Math.Abs(shapeAt(Math.Max(0.0, u - 0.05)) - here) < 1e-6 && Math.Abs(shapeAt(Math.Min(1.0, u + 0.05)) - here) < 1e-6)
                    exact.Add(u);
            }
            List<double> points = FitRisingUserCurve(shapeAt, exact, bounds);
            curve.Curvature = points ?? FitUserCurve(shapeAt, exact);
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

        // sign * spline(At) <= Bound.
        public struct ShapeBound
        {
            public double At;
            public double Sign;
            public double Bound;

            public ShapeBound(double at, double sign, double bound)
            {
                At = at;
                Sign = sign;
                Bound = bound;
            }
        }

        // FitUserCurve that never falls: least squares on 201 points, exact at the given positions (and the ends), the
        // spline non-decreasing on 401 points and within the bounds. Active-set method on the KKT system: start without
        // inequalities, add the most violated one, drop one whose multiplier turns negative. null when it finds no
        // solution (the caller falls back); points rounded to 4 decimals (rounding keeps their order).
        public static List<double> FitRisingUserCurve(Func<double, double> shape, IEnumerable<double> exactAt, IEnumerable<ShapeBound> bounds)
        {
            int n = AxisModels.UserCurvePoints;
            const int m = 201;
            const int grid = 401;
            double[,] h = new double[n, n];
            double[] r = new double[n];
            for (int k = 0; k < m; k++)
            {
                double u = k / (double)(m - 1);
                double[] row = BasisRow(u);
                double target = shape(u);
                for (int i = 0; i < n; i++)
                {
                    r[i] += 2.0 * row[i] * target;
                    for (int j = 0; j < n; j++)
                        h[i, j] += 2.0 * row[i] * row[j];
                }
            }
            List<double[]> eqRows = new List<double[]>();
            List<double> eqValues = new List<double>();
            List<double> eqAt = new List<double>();
            foreach (double at in (exactAt ?? Enumerable.Empty<double>()).Concat(new[] { 0.0, 1.0 }))
            {
                double u = Math.Max(0.0, Math.Min(1.0, at));
                if (eqAt.Any(e => Math.Abs(e - u) < 1e-6))
                    continue;
                eqAt.Add(u);
                eqRows.Add(BasisRow(u));
                eqValues.Add(shape(u));
            }
            // Inequalities G c <= g: the spline rises between neighbouring grid points, then the bounds.
            List<double[]> inRows = new List<double[]>();
            List<double> inValues = new List<double>();
            double[] previous = BasisRow(0.0);
            for (int k = 1; k < grid; k++)
            {
                double[] next = BasisRow(k / (double)(grid - 1));
                double[] row = new double[n];
                for (int j = 0; j < n; j++)
                    row[j] = previous[j] - next[j];
                inRows.Add(row);
                inValues.Add(0.0);
                previous = next;
            }
            foreach (ShapeBound b in bounds ?? Enumerable.Empty<ShapeBound>())
            {
                double[] row = BasisRow(Math.Max(0.0, Math.Min(1.0, b.At)));
                inRows.Add(row.Select(v => v * b.Sign).ToArray());
                inValues.Add(b.Bound);
            }

            List<int> active = new List<int>();
            HashSet<int> blocked = new HashSet<int>();
            for (int iteration = 0; iteration < 400; iteration++)
            {
                int rows = eqRows.Count + active.Count;
                int size = n + rows;
                double[,] a = new double[size, size + 1];
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++)
                        a[i, j] = h[i, j];
                    a[i, size] = r[i];
                }
                for (int c = 0; c < rows; c++)
                {
                    double[] row = c < eqRows.Count ? eqRows[c] : inRows[active[c - eqRows.Count]];
                    for (int j = 0; j < n; j++)
                    {
                        a[n + c, j] = row[j];
                        a[j, n + c] = row[j];
                    }
                    a[n + c, size] = c < eqRows.Count ? eqValues[c] : inValues[active[c - eqRows.Count]];
                }
                double[] solution = Solve(a, size);
                if (solution == null)
                {
                    // The last added constraint depends on the others: leave it out.
                    if (active.Count == 0)
                        return null;
                    blocked.Add(active[active.Count - 1]);
                    active.RemoveAt(active.Count - 1);
                    continue;
                }
                // An active inequality whose multiplier is negative pulls the wrong way: drop the worst.
                int drop = -1;
                double worst = -1e-10;
                for (int i = 0; i < active.Count; i++)
                {
                    double lambda = solution[n + eqRows.Count + i];
                    if (lambda < worst)
                    {
                        worst = lambda;
                        drop = i;
                    }
                }
                if (drop >= 0)
                {
                    active.RemoveAt(drop);
                    blocked.Clear();
                    continue;
                }
                int add = -1;
                double most = 1e-10;
                bool blockedViolated = false;
                for (int c = 0; c < inRows.Count; c++)
                {
                    if (active.Contains(c))
                        continue;
                    double value = -inValues[c];
                    for (int j = 0; j < n; j++)
                        value += inRows[c][j] * solution[j];
                    if (value <= most)
                        continue;
                    if (blocked.Contains(c))
                    {
                        blockedViolated |= value > 1e-7;
                        continue;
                    }
                    most = value;
                    add = c;
                }
                if (add >= 0)
                {
                    active.Add(add);
                    continue;
                }
                if (blockedViolated)
                    return null;
                List<double> curve = new List<double>();
                for (int j = 0; j < n; j++)
                {
                    double v = Math.Round(Math.Max(0.0, Math.Min(1.0, solution[j])), 4);
                    curve.Add(j > 0 ? Math.Max(curve[j - 1], v) : v);
                }
                return curve;
            }
            return null;
        }

        private static double[] BasisRow(double u)
        {
            double[] row = new double[AxisModels.UserCurvePoints];
            for (int j = 0; j < row.Length; j++)
                row[j] = SplineBasis(j, u);
            return row;
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
