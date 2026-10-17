#if NET
#nullable disable
#endif
using System;

// War Thunder's per-axis settings (Controls > axis row > Axis setup), machine.blk controls{axes{<axis>{...}}} in file
// units. The curve is the game's own preview formula (its Axis setup script, axiscontrols.nut onAxisInputTimer):
//   v = (inverse ? -x : x) * kMul + kAdd;  s = sign(v) (before clamping);  v = clamp(v, -1, 1)
//   v = |v| < innerDeadzone ? 0 : (|v| - innerDeadzone) / (1 - innerDeadzone);  y = s * |v|^(1 + nonlinearity)
// then (inferred, not in the scripts) the -1..1 result spans rangeMin..rangeMax (throttle and collective 0..1).
// See docs/games/war-thunder.md.
namespace JoyPro.Engine.Axis
{
    internal static class WarThunderAxisModel
    {
        public const string Id = "warthunder";

        // The game's limits (controlsConsts.nut MAX_DEADZONE 0.5, MAX_NONLINEARITY 4 shown = 3 in the file).
        public const double MaxDeadzone = 0.5;
        public const double MaxNonlinearity = 3.0;

        [GameAxisModel("WarThunder")]
        private static AxisModel Build()
        {
            return new AxisModel
            {
                Id = Id,
                Title = "War Thunder",
                Description = "Per axis function (machine.blk axes{}), all aircraft: dead zone, nonlinearity, multiplier, correction, invert, relative control (sensitivity, step), keep value for a disabled axis, output range; file units (the Axis setup dialog shows nonlinearity + 1 and the correction x 50).",
                Parameters =
                {
                    new AxisParameter { Key = "innerDeadzone", Label = "Dead zone", Kind = AxisParameterKind.Number, Min = 0, Max = MaxDeadzone, Step = 0.025, Default = 0.05,
                        Help = "Share of the (multiplied) axis around its centre that stays 0; the game shows 0.00..0.50." },
                    new AxisParameter { Key = "nonlinearity", Label = "Nonlinearity", Kind = AxisParameterKind.Number, Min = 0, Max = MaxNonlinearity, Step = 0.1, Default = 0,
                        Help = "Output = input ^ (1 + nonlinearity); the game shows 1.0..4.0 (this value + 1)." },
                    new AxisParameter { Key = "kMul", Label = "Multiplier", Kind = AxisParameterKind.Number, Min = 0.02, Max = 2, Step = 0.05, Default = 1,
                        Help = "Scales the axis before the dead zone; above 1 full output comes before the end of the travel (the game's slider: 0.20..2.00)." },
                    new AxisParameter { Key = "kAdd", Label = "Correction", Kind = AxisParameterKind.Number, Min = -1, Max = 1, Step = 0.02, Default = 0,
                        Help = "Added after the multiplier (centre offset); the game shows -50..50 (this value x 50)." },
                    new AxisParameter { Key = "relative", Label = "Relative control", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 1, Default = 0,
                        Help = "1: the axis sets the speed of change instead of the position (not offered for roll and pitch)." },
                    new AxisParameter { Key = "relSens", Label = "Relative control: sensitivity", Kind = AxisParameterKind.Number, Min = 0.01, Max = 1, Step = 0.01, Default = 1,
                        Help = "Speed of a relative axis; the game shows 10..100 % (= sqrt(value) x 100)." },
                    new AxisParameter { Key = "relStep", Label = "Relative control: step", Kind = AxisParameterKind.Number, Min = 0, Max = 0.5, Step = 0.01, Default = 0,
                        Help = "Step of a relative axis; the game shows 0..50 (value x 100)." },
                    new AxisParameter { Key = "keepDisabledValue", Label = "Keep value for disabled axis", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 1, Default = 0,
                        Help = "1: the axis keeps its last value while it is switched off (its enable shortcut)." },
                    new AxisParameter { Key = "rangeMin", Label = "Output range: minimum", Kind = AxisParameterKind.Number, Min = -1, Max = 1, Step = 0.01, Default = -1,
                        Help = "Value at the start of the travel; 0 for throttle and collective (file only, the game has no slider for it)." },
                    new AxisParameter { Key = "rangeMax", Label = "Output range: maximum", Kind = AxisParameterKind.Number, Min = -1, Max = 1, Step = 0.01, Default = 1,
                        Help = "Value at the end of the travel (file only)." },
                    new AxisParameter { Key = "slider", Label = "One-sided axis (throttle, collective, ...)", Kind = AxisParameterKind.Toggle },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = Evaluate,
                Fit = Fit
            };
        }

        // The curve of the Axis setup dialog. The preview's output is -1..1 over the axis's range: a one-sided axis
        // (Slider; rangeMin 0 by default) shows its 0..1 value as -1..1 like the other models' sliders.
        public static double Evaluate(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            if (t.Inverted)
                x = -x;
            double v = x * t.NativeOr("kMul", 1.0) + t.NativeOr("kAdd", 0.0);
            double sign = v < 0 ? -1.0 : 1.0;
            v = Math.Min(1.0, Math.Abs(v));
            double dz = Math.Max(0.0, Math.Min(0.999, t.NativeOr("innerDeadzone", 0.05)));
            v = v < dz ? 0.0 : (v - dz) / (1.0 - dz);
            double y = sign * Math.Pow(v, 1.0 + Math.Max(0.0, t.NativeOr("nonlinearity", 0.0)));
            double lo = t.NativeOr("rangeMin", t.Slider ? 0.0 : -1.0);
            double hi = t.NativeOr("rangeMax", 1.0);
            double game = lo + (y + 1.0) / 2.0 * (hi - lo);
            double shown = t.Slider ? 2.0 * game - 1.0 : game;
            return Math.Max(-1.0, Math.Min(1.0, shown));
        }

        // The base (DCS) values in War Thunder's terms:
        //   centred axis: multiplier = 1 / saturation X and dead zone = DCS dead zone x multiplier reproduce DCS's
        //   linear part exactly; the nonlinearity (0..3 in steps of 0.1) that comes closest to the base curve covers
        //   curvature, user curve and saturation Y (least squares on 20 points).
        //   one-sided axis (Slider): no centre dead zone (it would sit at half throttle), multiplier and correction
        //   map the base's travel between dead zone and saturation X onto the whole range (exact for a linear slider).
        // Relative control, keep value and the output range are not part of the base and stay as they are in the file.
        private static AxisTuning Fit(AxisTuning tuning, Func<double, double> target)
        {
            AxisTuning result = tuning.Clone();
            double dz = Math.Max(0.0, Math.Min(1.0, tuning.Deadzone));
            double sx = tuning.SaturationX <= 0 ? 1.0 : Math.Min(1.0, tuning.SaturationX);
            if (tuning.Slider)
            {
                double span = Math.Max(0.5, sx - dz);
                double kMul = Math.Max(0.2, Math.Min(2.0, 1.0 / span));
                result.Native["kMul"] = Math.Round(kMul, 4);
                result.Native["kAdd"] = Math.Round(Math.Max(-1.0, Math.Min(1.0, -1.0 - (2.0 * dz - 1.0) * kMul)), 4);
                result.Native["innerDeadzone"] = 0.0;
                result.Native["nonlinearity"] = 0.0;
                return result;
            }
            double mul = Math.Max(0.2, Math.Min(2.0, 1.0 / sx));
            result.Native["kMul"] = Math.Round(mul, 4);
            result.Native["kAdd"] = 0.0;
            result.Native["innerDeadzone"] = Math.Round(Math.Min(MaxDeadzone, dz * mul), 4);
            AxisTuning probe = result.Clone();
            probe.Inverted = false;
            probe.Slider = false;
            probe.Native.Remove("rangeMin");
            probe.Native.Remove("rangeMax");
            double best = double.MaxValue;
            double bestN = 0.0;
            for (int n = 0; n <= 30; n++)
            {
                probe.Native["nonlinearity"] = n / 10.0;
                double err = 0.0;
                for (int i = 1; i <= 20; i++)
                {
                    double x = i / 20.0;
                    double diff = Evaluate(probe, x) - target(x);
                    err += diff * diff;
                }
                if (err < best - 1e-12)
                {
                    best = err;
                    bestN = n / 10.0;
                }
            }
            result.Native["nonlinearity"] = bestN;
            return result;
        }
    }
}
