#if NET
#nullable disable
#endif
using System;

// Project Wingman: Settings > Controls > CURVES + DEADZONES, one row per axis action (Pitch, Roll, Thrust, Yaw, View
// Padlock X/Y): CURVE (the menu shows 0.00-2.00, default 1.00; savegame.sav stores half of it, <Axis>Curve 0..1) and
// DEADZONE (0-100 %, <Axis>Deadzone 0..1; the game's defaults: pitch/roll 25 %, view 10 %, thrust/yaw 0 %). The values
// belong to the action, not to a device. See docs/games/project-wingman.md.
namespace JoyPro.Engine.Axis
{
    public static class ProjectWingmanAxisModel
    {
        public const string Id = "projectwingman";
        public const double CurveMax = 2.0;

        [GameAxisModel("ProjectWingman")]
        private static AxisModel Build()
        {
            return new AxisModel
            {
                Id = Id,
                Title = "Project Wingman",
                Description = "Per axis action (Pitch, Roll, Thrust, Yaw, View X/Y), for all devices together: curve (menu 0.00-2.00, 1.00 = straight) and dead zone (0-100 %), as the CURVES + DEADZONES page shows them (savegame.sav). The formula is not readable (nativized blueprint); the preview assumes a rescaled dead zone and the curve as an exponent (unverified).",
                Parameters =
                {
                    new AxisParameter { Key = "curve", Label = "Curve", Kind = AxisParameterKind.Number, Min = 0, Max = CurveMax, Step = 0.01, Default = 1,
                        Help = "The menu's CURVE value: 1.00 is straight, higher values make the centre gentler (assumed exponent)." },
                    new AxisParameter { Key = "deadZone", Label = "Dead zone", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 0,
                        Help = "Share of the travel around the centre that is ignored (the menu's percent / 100). The game's defaults: pitch and roll 0.25, view 0.10, thrust and yaw 0." },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = Evaluate,
                Fit = Fit
            };
        }

        public static double Curve(AxisTuning t)
        {
            return Math.Round(Math.Max(0.0, Math.Min(CurveMax, t.NativeOr("curve", 1.0))), 2);
        }

        public static double DeadZone(AxisTuning t)
        {
            return Math.Round(Math.Max(0.0, Math.Min(1.0, t.NativeOr("deadZone", t.Deadzone))), 2);
        }

        // The prefix only documents which row the value is for (the defaults differ per row, the formula does not).
        public static double DeadZone(AxisTuning t, string axisPrefix)
        {
            return DeadZone(t);
        }

        // Working hypothesis: invert, cut the dead zone and rescale the rest to 0..1, raise to the curve value.
        public static double Evaluate(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            if (t.Inverted)
                x = -x;
            double dz = DeadZone(t);
            double u = Math.Abs(x);
            if (u <= dz || dz >= 1.0)
                return 0.0;
            double v = (u - dz) / (1.0 - dz);
            double c = Curve(t);
            return Math.Sign(x) * (c <= 0 ? 1.0 : Math.Pow(v, c));
        }

        // The base curve as Project Wingman can hold it: the centre dead zone carries over (a throttle's start dead zone
        // does not: the game treats every axis as centred), the curve value is the exponent that fits the base curve best.
        private static AxisTuning Fit(AxisTuning tuning, Func<double, double> target)
        {
            AxisTuning result = tuning.Clone();
            double dz = tuning.Slider ? 0.0 : Math.Round(Math.Max(0.0, Math.Min(1.0, tuning.Deadzone)), 2);
            result.Native["deadZone"] = dz;
            double best = 1.0;
            double bestError = double.MaxValue;
            for (int i = 10; i <= 200; i++)
            {
                double c = i / 100.0;
                double error = 0;
                for (int k = 1; k <= 20; k++)
                {
                    double x = k / 20.0;
                    double v = x <= dz ? 0.0 : Math.Pow((x - dz) / (1.0 - dz), c);
                    double d = v - target(x);
                    error += d * d;
                }
                if (error < bestError - 1e-12)
                {
                    bestError = error;
                    best = c;
                }
            }
            result.Native["curve"] = best;
            return result;
        }
    }
}
