#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Linq;

// X-Plane 11/12 per-axis response curve (Settings > Joystick > axis > "Add/Edit Response Curve"), stored by the game
// as one line per hardware axis in "... Joysticks.prf":
//   axis_response_curve<i> [FIXED_RANGE_TYPE <type> FIXED_PT_0 <n> FIXED_PT_1 <n> FIXED_PT_2 <n> ; ]INTERP_MODE <mode> | x y  | x y  ...
// Stops (x, y) in 0..1: x = deflection magnitude on centred axes, travel on one-sided axes. See docs/games/xplane.md.
namespace JoyPro.Engine.Axis
{
    public static class XPlaneAxisModel
    {
        public const string Id = "xplane";

        // INTERP_MODE values as the game writes them (exe strings; LINEAR, CATMULL_ROM, CUBIC_AVG seen in user files).
        public static readonly string[] InterpModes = { "LINEAR", "CATMULL_ROM", "CUBIC_AVG" };

        // FIXED_RANGE_TYPE values (XP12 exe string table, in its order); index 0 = no fixed range.
        public static readonly string[] RangeTypes =
        {
            string.Empty, "beta_reverse_range", "feather_range", "mixture_cutoff_range", "helo_thro_detent_range",
            "fadec_thro_range", "fadec_thro_with_reverse_range", "feathershutoff_range"
        };

        [GameAxisModel("XPlane12", "XPlane11", "XPlane")]
        private static AxisModel Build()
        {
            return new AxisModel
            {
                Id = Id,
                Title = "X-Plane 11/12",
                Description = "Per hardware axis (Joysticks.prf axis_response_curve): a response curve with any number of stops and LINEAR, CATMULL_ROM or CUBIC_AVG interpolation, " +
                              "throttle/prop/mixture fixed ranges (beta & reverse, feather, cutoff, FADEC), reverse. Dead zone, saturation, curvature and the 11-point curve become the curve's stops; " +
                              "a curve imported from X-Plane keeps its exact stops until one of those changes. Null zone and centre are X-Plane's calibration and are never written. " +
                              "Control sensitivity, stability augmentation and control-loaded axes are game settings (engine ffb ... --game XPlane).",
                Parameters =
                {
                    new AxisParameter { Key = "deadzone", Label = "Dead zone (flat first stop)", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 0 },
                    new AxisParameter { Key = "saturationX", Label = "Saturation X", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 1 },
                    new AxisParameter { Key = "saturationY", Label = "Saturation Y", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 1 },
                    new AxisParameter { Key = "curvature", Label = "Curvature", Kind = AxisParameterKind.Number, Min = -1, Max = 1, Step = 0.01, Default = 0 },
                    new AxisParameter { Key = "userCurve", Label = "User curve (11 equally spaced stops)", Kind = AxisParameterKind.UserCurve, Min = 0, Max = 1, Step = 0.01, Default = 0 },
                    new AxisParameter { Key = "interp", Label = "Interpolation", Kind = AxisParameterKind.Number, Min = 0, Max = 2, Step = 1, Default = 0,
                        Help = "0 LINEAR (straight lines between the stops), 1 CATMULL_ROM, 2 CUBIC_AVG (smooth curves through the stops; preview checked against X-Plane 12.4 in game)." },
                    new AxisParameter { Key = "rangeType", Label = "Fixed range (throttle, prop, mixture)", Kind = AxisParameterKind.Number, Min = 0, Max = 7, Step = 1, Default = 0,
                        Help = "0 none, 1 beta & reverse (throttle), 2 feather (prop), 3 mixture cutoff (mixture), 4 helicopter throttle detent (throttle), 5 FADEC throttle, 6 FADEC throttle with reverse, 7 feather + shutoff (prop). Written only on axes of that kind." },
                    new AxisParameter { Key = "rangePt0", Label = "Fixed range point 1", Kind = AxisParameterKind.Number, Min = 0, Max = 100, Step = 1, Default = 0,
                        Help = "FIXED_PT_0 of the fixed range (an integer; % of travel assumed, not yet checked in the game)." },
                    new AxisParameter { Key = "rangePt1", Label = "Fixed range point 2", Kind = AxisParameterKind.Number, Min = 0, Max = 100, Step = 1, Default = 0,
                        Help = "FIXED_PT_1 of the fixed range (integer)." },
                    new AxisParameter { Key = "rangePt2", Label = "Fixed range point 3", Kind = AxisParameterKind.Number, Min = 0, Max = 100, Step = 1, Default = 0,
                        Help = "FIXED_PT_2 of the fixed range (integer)." },
                    new AxisParameter { Key = "slider", Label = "One-sided axis (throttle, brakes, ...)", Kind = AxisParameterKind.Toggle },
                    new AxisParameter { Key = "invert", Label = "Reverse", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = Evaluate,
                Fit = Fit,
                CurveStops = CurvePoints
            };
        }

        public static int Interp(AxisTuning t)
        {
            return (int)Math.Max(0, Math.Min(InterpModes.Length - 1, Math.Round(t.NativeOr("interp", 0))));
        }

        public static int RangeType(AxisTuning t)
        {
            return (int)Math.Max(0, Math.Min(RangeTypes.Length - 1, Math.Round(t.NativeOr("rangeType", 0))));
        }

        // Stick -1..1 (sliders: -1 = start) -> X-Plane's curve output (before X-Plane's calibration, null zone and the
        // control sensitivity of curve-less pitch/roll/yaw axes, which this preview does not model). Reverse is applied
        // to the input. JoyPro's (DCS's) direction: X-Plane's own flip of the throttle uses (docs/games/xplane.md) is
        // not shown, as DCS's thrust axes flip the same way.
        public static double Evaluate(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            if (t.Inverted)
                x = -x;
            List<KeyValuePair<double, double>> points = CurvePoints(t);
            int mode = Interp(t);
            if (t.Slider)
                return 2.0 * Interpolate(points, mode, (x + 1.0) / 2.0) - 1.0;
            double sign = x < 0 ? -1.0 : 1.0;
            return sign * Interpolate(points, mode, Math.Abs(x));
        }

        // The stops X-Plane gets: the explicit ones (imported, or set with --curve-points), else the tuning's dead zone,
        // saturation and curvature / 11-point curve as stops.
        public static List<KeyValuePair<double, double>> CurvePoints(AxisTuning t)
        {
            if (t.HasControlPoints)
                return t.ControlPoints().OrderBy(p => p.Key).ToList();
            double dz = Math.Max(0.0, Math.Min(0.99, t.Deadzone));
            double sx = t.SaturationX <= 0 ? 1.0 : Math.Min(1.0, t.SaturationX);
            if (sx <= dz)
                sx = Math.Min(1.0, dz + 0.01);
            double sy = Math.Max(0.0, Math.Min(1.0, t.SaturationY));
            List<KeyValuePair<double, double>> points = new List<KeyValuePair<double, double>>();
            if (dz > 0)
                points.Add(new KeyValuePair<double, double>(0.0, 0.0));
            if (t.IsUserCurve)
            {
                for (int i = 0; i < AxisModels.UserCurvePoints; i++)
                    points.Add(new KeyValuePair<double, double>(dz + (sx - dz) * i / (AxisModels.UserCurvePoints - 1), Math.Max(0.0, Math.Min(1.0, t.Curvature[i]))));
            }
            else
            {
                double k = t.Curvature == null || t.Curvature.Count == 0 ? 0.0 : t.Curvature[0];
                if (Math.Abs(k) < 1e-9)
                {
                    points.Add(new KeyValuePair<double, double>(dz, 0.0));
                    points.Add(new KeyValuePair<double, double>(sx, sy));
                }
                else
                {
                    AxisModel dcs = AxisModels.Get(AxisModels.Dcs);
                    AxisTuning shape = new AxisTuning { Deadzone = dz, SaturationX = sx, SaturationY = sy, Curvature = new List<double> { k } };
                    foreach (KeyValuePair<double, double> p in SampleLinear(u => dcs.Evaluate(shape, u), new[] { dz, sx }))
                    {
                        if (p.Key >= dz - 1e-12 && p.Key <= sx + 1e-12)
                            points.Add(p);
                    }
                }
            }
            if (sx < 1.0)
                points.Add(new KeyValuePair<double, double>(1.0, sy));
            return Dedupe(points);
        }

        // Base tuning without X-Plane values: its curve as LINEAR stops (exactly what X-Plane interpolates), dense where
        // the curve bends; dead zone and saturation become stops of their own.
        private static AxisTuning Fit(AxisTuning tuning, Func<double, double> target)
        {
            AxisTuning result = tuning.Clone();
            double dz = Math.Max(0.0, Math.Min(0.99, tuning.Deadzone));
            double sx = tuning.SaturationX <= 0 ? 1.0 : Math.Min(1.0, tuning.SaturationX);
            result.SetControlPoints(Dedupe(SampleLinear(u => Math.Max(0.0, Math.Min(1.0, target(u))), new[] { dz, sx })));
            result.Native["interp"] = 0;
            return result;
        }

        // Stops on 0..1 whose straight lines stay within 0.001 of f (breaks: kinks such as dead zone and saturation).
        private static List<KeyValuePair<double, double>> SampleLinear(Func<double, double> f, IEnumerable<double> breaks)
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
            foreach (double t in new[] { 0.25, 0.5, 0.75 })
            {
                double x = a + (b - a) * t;
                if (Math.Abs(f(x) - (fa + (fb - fa) * t)) > 0.001)
                    close = false;
            }
            if (close || depth >= 8 || b - a < 1.0 / 512)
                return;
            double m = (a + b) / 2.0;
            double fm = f(m);
            Subdivide(f, a, fa, m, fm, depth + 1, points);
            points.Add(new KeyValuePair<double, double>(m, fm));
            Subdivide(f, m, fm, b, fb, depth + 1, points);
        }

        private static List<KeyValuePair<double, double>> Dedupe(List<KeyValuePair<double, double>> points)
        {
            List<KeyValuePair<double, double>> result = new List<KeyValuePair<double, double>>();
            foreach (KeyValuePair<double, double> p in points.OrderBy(p => p.Key))
            {
                if (result.Count > 0 && Math.Abs(result[result.Count - 1].Key - p.Key) < 1e-6)
                    result[result.Count - 1] = p;
                else
                    result.Add(p);
            }
            return result;
        }

        // A curve that changes nothing (y = x), so no curve line is needed.
        public static bool IsIdentity(List<KeyValuePair<double, double>> points)
        {
            if (points == null || points.Count < 2)
                return true;
            return Math.Abs(points[0].Key) < 1e-6 && Math.Abs(points[points.Count - 1].Key - 1.0) < 1e-6 && points.All(p => Math.Abs(p.Key - p.Value) < 1e-6);
        }

        // X-Plane's interpolation between the stops (u in 0..1), measured in game (X-Plane 12.4, XPlaneCurves.ps1:
        // within 0.0001 over 40 positions per mode). LINEAR: straight lines. CATMULL_ROM: uniform Catmull-Rom per
        // segment, t linear in x, tangent (y[i+1] - y[i-1]) / 2 per segment (not per x, so the slope jumps at stops
        // with unequal neighbours), the end stops repeated. CUBIC_AVG: cubic Hermite in x, slope at a stop = mean of
        // the two adjacent slopes, 0 at the first stop [the last stop's 0 is by symmetry, not measured]. Clamped to 0..1.
        public static double Interpolate(List<KeyValuePair<double, double>> points, int mode, double u)
        {
            if (points == null || points.Count == 0)
                return u;
            int n = points.Count;
            if (u <= points[0].Key)
                return points[0].Value;
            if (u >= points[n - 1].Key)
                return points[n - 1].Value;
            int i = 0;
            while (i < n - 2 && u > points[i + 1].Key)
                i++;
            double x0 = points[i].Key;
            double x1 = points[i + 1].Key;
            double y0 = points[i].Value;
            double y1 = points[i + 1].Value;
            double h = x1 - x0;
            if (h <= 1e-12)
                return y1;
            double t = (u - x0) / h;
            if (mode == 0 || n == 2)
                return y0 + (y1 - y0) * t;
            // Tangents in units of t: Catmull-Rom's directly, CUBIC_AVG's slopes times the segment width.
            double m0 = mode == 1 ? CatmullTangent(points, i) : h * Tangent(points, i);
            double m1 = mode == 1 ? CatmullTangent(points, i + 1) : h * Tangent(points, i + 1);
            double t2 = t * t;
            double t3 = t2 * t;
            double y = (2 * t3 - 3 * t2 + 1) * y0 + (t3 - 2 * t2 + t) * m0 + (-2 * t3 + 3 * t2) * y1 + (t3 - t2) * m1;
            return Math.Max(0.0, Math.Min(1.0, y));
        }

        private static double Slope(List<KeyValuePair<double, double>> p, int j)
        {
            double dx = p[j + 1].Key - p[j].Key;
            return dx <= 1e-12 ? 0.0 : (p[j + 1].Value - p[j].Value) / dx;
        }

        private static double CatmullTangent(List<KeyValuePair<double, double>> p, int i)
        {
            int n = p.Count;
            return (p[Math.Min(n - 1, i + 1)].Value - p[Math.Max(0, i - 1)].Value) / 2.0;
        }

        private static double Tangent(List<KeyValuePair<double, double>> p, int i)
        {
            if (i == 0 || i == p.Count - 1)
                return 0.0;
            return (Slope(p, i - 1) + Slope(p, i)) / 2.0;
        }
    }
}
